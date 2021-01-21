using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Puerts;
using UnityEngine;

public class JsWorker : MonoBehaviour, IDisposable
{
    public static JsWorker New(ILoader loader, string filepath)
    {
        var obj = new GameObject("JsWorker");
        DontDestroyOnLoad(obj);
        var ins = obj.AddComponent<JsWorker>();
        ins.Loader = new _SyncLoader(loader);
        if (!string.IsNullOrEmpty(filepath))
            ins.Working(filepath);

        return ins;
    }
    public static JsWorker New(ILoader loader)
    {
        return New(loader, null);
    }

    /// <summary>
    /// jsWorker.ts脚本
    /// </summary>
    private const string JS_WORKER = "require('./common/jsWorker')";
    /// <summary>
    /// 一次处理的事件数量
    /// </summary>
    private const int PROCESS_COUNT = 5;

    public JsEnv JsEnv { get; private set; }
    //消息接口
    public Func<string, Package, Package> messageByMain { get; set; }
    public Func<string, Package, Package> messageByChild { get; set; }
    //同步操作
    public SyncProcess Sync { get; private set; }
    private _SyncLoader Loader;
    //线程运行中, 且JsEnv准备完成
    public bool IsAlive
    {
        get
        {
            return this.thread != null && this.thread.IsAlive
                && this.JsEnv != null;
        }
    }

    //线程
    private Thread thread;
    private bool running = false;
    private bool finish = false;
    //消息集合
    private Queue<_Event> mainEvents;
    private Queue<_Event> childEvents;
    //Require list
    private Queue<(string, string)> eval;

    public JsWorker()
    {
        mainEvents = new Queue<_Event>();
        childEvents = new Queue<_Event>();
        eval = new Queue<(string, string)>();
        Sync = new SyncProcess(this);
    }
    void Start()
    {
        if (Loader == null)
        {
            this.enabled = false;
            throw new Exception("Loader is null");
        }
    }
    void Update()
    {
        ProcessMain();
        Sync.ProcessMain();
        Loader.ProcessMain();
    }
    void OnDestroy()
    {
        Dispose();
    }
    void Working(string filepath)
    {
        if (this.JsEnv != null || this.thread != null || this.running)
            throw new Exception("JsWorker已经在运行中, 无法重复启动");
        if (this.Loader == null)
            throw new Exception("JsWorker.Loader为空, 它无法正常运行");
        if (this.finish)
            throw new Exception("JsWorker已被释放(关闭), 无法重新启动");
        if (!this.enabled)
            throw new Exception("JsWorker主线程被禁用, 它无法正常运行");

        running = true;
        thread = new Thread(new ThreadStart(() =>
        {
            JsEnv jsEnv = null;
            try
            {
                // JsEnv脚本放在Resource目录下,故ILoader仅允许在主线程调用
                // 子线程_SyncLoader接口会阻塞线程, 直到主线程调用ILoader后才会继续执行
                // JsEnv初始化时将调用_SyncLoader接口
                jsEnv = JsEnv = new JsEnv(Loader);
                jsEnv.UsingAction<string, string>();
                jsEnv.UsingAction<string, Package>();
                jsEnv.UsingFunc<string, Package, object>();
                jsEnv.UsingFunc<string, Package, Package>();
                jsEnv.Eval(JS_WORKER);
                jsEnv.Eval<Action<JsWorker>>(@"(function (_w){ (this ?? globalThis)['globalWorker'] = new JsWorker(_w); })")(this);
                jsEnv.Eval(string.Format("require(\"{0}\")", filepath));
                while (running)
                {
                    if (JsEnv == null)
                        break;
                    Thread.Sleep(20);
                    jsEnv.Tick();
                    ProcessChild();
                    ProcessChildEval(jsEnv);
                    Sync.ProcessChild();
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning(e.Message);
            }
            finally
            {
                jsEnv?.Dispose();
                JsEnv = null;
            }
        }));
        thread.IsBackground = true;
        thread.Start();
    }
    public void Startup(string filepath)
    {
        Working(filepath);
    }
    public void Dispose()
    {
        messageByMain = null;
        messageByChild = null;
        running = false;
        finish = true;

        //此处仅通知线程中断, 由线程自行结束(使用Abort阻塞将导致puerts crash)
        if (thread != null) thread.Interrupt();
        //if (JsEnv != null) JsEnv.Dispose();
        JsEnv = null;
        thread = null;
    }
    public void CallMain(string name, Package data)
    {
        lock (mainEvents)
        {
            mainEvents.Enqueue(new _Event()
            {
                name = name,
                data = data
            });
        }
    }
    public void CallChild(string name, Package data)
    {
        lock (childEvents)
        {
            childEvents.Enqueue(new _Event()
            {
                name = name,
                data = data
            });
        }
    }
    public void Eval(string chunk, string chunkName = "chunk")
    {
        if (chunk == null)
            return;
        lock (eval)
        {
            eval.Enqueue((chunk, chunkName));
        }
    }
    private void ProcessMain()
    {
        if (mainEvents.Count > 0)
        {
            List<_Event> events = new List<_Event>();
            lock (mainEvents)
            {
                int count = PROCESS_COUNT;
                while (count-- > 0 && mainEvents.Count > 0)
                    events.Add(mainEvents.Dequeue());
            }
            Func<string, Package, Package> func = this.messageByMain;
            if (func != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    try
                    {
                        func(events[i].name, events[i].data);
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogError(e.Message);
                    }
                }
            }
        }
    }
    private void ProcessChild()
    {
        if (childEvents.Count > 0)
        {
            List<_Event> events = new List<_Event>();
            lock (childEvents)
            {
                int count = PROCESS_COUNT;
                while (count-- > 0 && childEvents.Count > 0)
                    events.Add(childEvents.Dequeue());
            }
            Func<string, Package, Package> func = this.messageByChild;
            if (func != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    try
                    {
                        func(events[i].name, events[i].data);
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogError(e.Message);
                    }
                }
            }
        }
    }
    private void ProcessChildEval(JsEnv jsEnv)
    {
        if (eval.Count > 0)
        {
            List<(string, string)> chunks = new List<(string, string)>();
            lock (eval)
            {
                int count = PROCESS_COUNT;
                while (count-- > 0 && eval.Count > 0)
                    chunks.Add(eval.Dequeue());
            }
            for (int i = 0; i < chunks.Count; i++)
            {
                try
                {
                    var chunk = chunks[i];
                    jsEnv.Eval(chunk.Item1, chunk.Item2);
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogError(e.Message);
                }
            }
        }
    }

    private class _SyncLoader : ILoader
    {
        //脚本缓存
        private Dictionary<string, string> scripts;
        private Dictionary<string, string> debugPaths;
        private Dictionary<string, bool> state;
        //这个ILoader只能在主线程调用, 而本实例化对象在子线程中使用需要通过主线程同步
        private ILoader loader;
        //线程安全
        private ReaderWriterLock locker = new ReaderWriterLock();
        private const int lockTimeout = 1000;
        //加载内容
        private string filePath = null;
        private bool fileExists = false;
        private string readPath = null;
        private string readContent = null;
        private string debugpath = null;

        public _SyncLoader(ILoader loader)
        {
            this.loader = loader;
            this.scripts = new Dictionary<string, string>();
            this.debugPaths = new Dictionary<string, string>();
            this.state = new Dictionary<string, bool>();
        }

        public bool FileExists(string filepath)
        {
            bool result = false;
            if (this.state.TryGetValue(filepath, out result))
                return result;
            //写入主线程
            locker.AcquireWriterLock(lockTimeout);
            this.filePath = filepath;
            this.fileExists = false;
            locker.ReleaseWriterLock();
            //检测主线程状态
            try
            {
                while (true)
                {
                    locker.AcquireReaderLock(lockTimeout);
                    if (this.filePath == null)
                        break;
                    locker.ReleaseReaderLock();
                }
                this.state.Add(filepath, this.fileExists);
                return this.fileExists;
            }
            finally
            {
                locker.ReleaseReaderLock();
            }
        }
        public string ReadFile(string filepath, out string debugpath)
        {
            string script = null;
            if (this.scripts.TryGetValue(filepath, out script))
            {
                debugpath = this.debugPaths[filepath];
                return script;
            }
            //写入主线程
            locker.AcquireWriterLock(lockTimeout);
            this.readPath = filepath;
            this.readContent = null;
            this.debugpath = null;
            locker.ReleaseWriterLock();
            //检测主线程状态
            try
            {
                while (true)
                {
                    locker.AcquireReaderLock(lockTimeout);
                    if (this.readPath == null)
                        break;
                    locker.ReleaseReaderLock();
                }
                this.scripts.Add(filepath, this.readContent);
                this.debugPaths.Add(filepath, this.debugpath);

                debugpath = this.debugpath;
                return this.readContent;
            }
            finally
            {
                locker.ReleaseReaderLock();
            }
        }

        //主线程驱动接口
        public void ProcessMain()
        {
            if (this.filePath != null || this.readPath != null)
            {
                try
                {
                    locker.AcquireWriterLock(lockTimeout);
                    if (this.filePath != null)
                    {
                        this.fileExists = loader.FileExists(this.filePath);
                        this.filePath = null;
                    }
                    if (this.readPath != null)
                    {
                        this.readContent = loader.ReadFile(this.readPath, out this.debugpath);
                        this.readPath = null;
                    }
                }
                catch (Exception e)
                {
                    this.filePath = null;
                    this.fileExists = false;
                    this.readPath = null;
                    this.readContent = null;
                    this.debugpath = null;
                    throw e;
                }
                finally
                {
                    locker.ReleaseWriterLock();
                }
            }
        }
    }
    private class _Event
    {
        public string name;
        public Package data;
    }
    public class SyncProcess
    {
        private JsWorker worker = null;
        //线程安全
        private ReaderWriterLock locker = new ReaderWriterLock();
        private const int lockTimeout = 1000;
        //同步状态
        private bool syncing;
        //同步消息
        private string mainEventName = null;
        private Package mainEventData = null;
        private string childEventName = null;
        private Package childEventData = null;

        public SyncProcess(JsWorker worker)
        {
            this.worker = worker;
            this.syncing = false;
        }

        public object CallMain(string name, Package data)
        {
            if (name == null) return null;
            //获取同步状态
            if (!LockSyncing())
                throw new Exception("无法访问线程, 目标正在等待同步");
            //写入主线程
            locker.AcquireWriterLock(lockTimeout);
            this.mainEventName = name;
            this.mainEventData = data;
            locker.ReleaseWriterLock();
            //检测主线程状态
            try
            {
                while (true)
                {
                    locker.AcquireReaderLock(lockTimeout);
                    if (this.mainEventName == null)
                        break;
                    locker.ReleaseReaderLock();
                }
                return this.mainEventData;
            }
            finally
            {
                locker.ReleaseReaderLock();
                UnlockSyncing();
            }
        }
        public object CallChild(string name, Package data)
        {
            if (name == null) return null;
            //获取同步状态
            if (!LockSyncing())
                throw new Exception("无法访问线程, 目标正在等待同步");
            //写入子线程
            locker.AcquireWriterLock(lockTimeout);
            this.childEventName = name;
            this.childEventData = data;
            locker.ReleaseWriterLock();
            //检测子线程状态
            try
            {
                while (true)
                {
                    locker.AcquireReaderLock(lockTimeout);
                    if (this.childEventName == null)
                        break;
                    locker.ReleaseReaderLock();
                }
                return this.childEventData;
            }
            finally
            {
                locker.ReleaseReaderLock();
                UnlockSyncing();
            }
        }
        public void ProcessMain()
        {
            if (this.mainEventName != null)
            {
                Func<string, Package, Package> func = this.worker.messageByMain;
                try
                {
                    locker.AcquireWriterLock(lockTimeout);
                    Package data = null;
                    if (this.mainEventName != null && func != null)
                        data = func(this.mainEventName, this.mainEventData);
                    this.mainEventData = data;
                }
                catch (Exception e)
                {
                    this.mainEventData = null;
                    throw e;
                }
                finally
                {
                    this.mainEventName = null;
                    locker.ReleaseWriterLock();
                }
            }
        }
        public void ProcessChild()
        {
            if (this.childEventName != null)
            {
                Func<string, Package, Package> func = this.worker.messageByChild;
                try
                {
                    locker.AcquireWriterLock(lockTimeout);
                    Package data = null;
                    if (this.childEventName != null && func != null)
                        data = func(this.childEventName, this.childEventData);
                    this.childEventData = data;
                }
                catch (Exception e)
                {
                    this.childEventData = null;
                    throw e;
                }
                finally
                {
                    this.childEventName = null;
                    locker.ReleaseWriterLock();
                }
            }
        }

        private bool LockSyncing()
        {
            lock (locker)
            {
                if (this.syncing) return false;
                this.syncing = true;
                return true;
            }
        }
        private void UnlockSyncing()
        {
            lock (locker)
            {
                this.syncing = false;
            }
        }
    }
    public class Package
    {
        /**此数据类型 */
        public Type type;
        /**数据内容 */
        public object value;
        /**数据信息 */
        public object info;
        /**如果是对象, 代表对象Id */
        public int id = -1;

        public static byte[] ToBytes(Puerts.ArrayBuffer value)
        {
            if (value != null)
            {
                var source = value.Bytes;
                var result = new byte[source.Length];
                Array.Copy(source, 0, result, 0, source.Length);
                return result;
            }
            return null;
        }
        public static Puerts.ArrayBuffer ToArrayBuffer(byte[] value)
        {
            if (value != null)
                return new Puerts.ArrayBuffer(value);
            return null;
        }
    }
    public enum Type
    {
        Unknown,
        Value,
        Object,
        Array,
        Function,
        /**ArrayBuffer类型为指针传递,如果直接传将会造成多线程共享内存crash */
        ArrayBuffer,
        RefObject
    }
}

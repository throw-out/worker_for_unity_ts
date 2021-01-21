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
        ins.loader = new SyncLoader(ins, loader);
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
    //线程初始完成, 且运行中
    public bool IsAlive
    {
        get
        {
            return this.thread != null && this.thread.IsAlive && this.JsEnv != null;
        }
    }
    //同步对象
    private SyncLoader loader;
    private SyncProcess sync;
    public SyncProcess Sync
    {
        get
        {
            if (!this.IsAlive)
                throw new Exception("Thread not ready ok, can't use it now.");
            return this.sync;
        }
    }
    //同步状态
    private bool syncing;
    //线程
    private Thread thread;
    private bool running = false;
    //消息集合
    private Queue<Event> mainEvents;
    private Queue<Event> childEvents;
    //Eval require list
    private Queue<(string, string)> eval;

    public JsWorker()
    {
        mainEvents = new Queue<Event>();
        childEvents = new Queue<Event>();
        eval = new Queue<(string, string)>();
        sync = new SyncProcess(this);
    }
    void Start()
    {
        if (loader == null)
        {
            this.enabled = false;
            throw new Exception("instance cannot working, loader is null");
        }
    }
    void Update()
    {
        ProcessMain();
        sync.ProcessMain();
        loader.ProcessMain();
    }
    void OnDestroy()
    {
        Dispose();
    }
    void Working(string filepath)
    {
        if (this.JsEnv != null || this.thread != null || this.running)
            throw new Exception("Thread is running, cannot start repeatedly!");
        if (this.loader == null)
            throw new Exception("Thread cannot start working, loader is null!");
        if (!this.enabled)
            throw new Exception("Thread cannot start working, main thread is disable");

        syncing = false;
        running = true;
        thread = new Thread(new ThreadStart(() =>
        {
            JsEnv jsEnv = null;
            try
            {
                // JsEnv脚本放在Resource目录下,故ILoader仅允许在主线程调用
                // 子线程_SyncLoader接口会阻塞线程, 直到主线程调用ILoader后才会继续执行
                // JsEnv初始化时将调用_SyncLoader接口
                jsEnv = JsEnv = new JsEnv(loader);
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
                    sync.ProcessChild();
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
            mainEvents.Enqueue(new Event()
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
            childEvents.Enqueue(new Event()
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
            List<Event> events = new List<Event>();
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
            List<Event> events = new List<Event>();
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
    /// <summary>
    /// 获取同步锁定, 返回是否成功
    /// (注:如果两条线程都锁定则会死锁(它们都在等待对方同步), 因此只能有一条线程锁定同步状态)
    /// </summary>
    internal bool AcquireSyncing()
    {
        lock (loader)
        {
            if (this.syncing) return false;
            this.syncing = true;
            return true;
        }
    }
    /// <summary> 释放同步锁定 </summary>
    internal void ReleaseSyncing()
    {
        lock (loader)
        {
            this.syncing = false;
        }
    }


    private class Event
    {
        public string name;
        public Package data;
    }
    private class SyncLoader : ILoader
    {
        private JsWorker worker = null;
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

        public SyncLoader(JsWorker worker, ILoader loader)
        {
            this.worker = worker;
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
            //获取同步状态
            if (!worker.AcquireSyncing())
                throw new Exception("Other thread is syncing!");
            //写入主线程
            locker.AcquireWriterLock(lockTimeout);
            this.filePath = filepath;
            this.fileExists = false;
            locker.ReleaseWriterLock();
            //等待主线程同步
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
                worker.ReleaseSyncing();
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
            //获取同步状态
            if (!worker.AcquireSyncing())
                throw new Exception("Other thread is syncing!");
            //写入主线程
            locker.AcquireWriterLock(lockTimeout);
            this.readPath = filepath;
            this.readContent = null;
            this.debugpath = null;
            locker.ReleaseWriterLock();
            //等待主线程同步
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
                worker.ReleaseSyncing();
            }
        }

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
    public class SyncProcess
    {
        private JsWorker worker = null;
        //线程安全
        private ReaderWriterLock locker = new ReaderWriterLock();
        private const int lockTimeout = 1000;
        //同步消息
        private string m_eventName = null;
        private string c_eventName = null;
        private Package m_eventData = null;
        private Package c_eventData = null;

        public SyncProcess(JsWorker worker)
        {
            this.worker = worker;
        }

        public object CallMain(string name, Package data, bool throwOnError = true)
        {
            if (name == null) return null;
            //获取同步状态
            if (!worker.AcquireSyncing())
            {
                if (!throwOnError) return null;
                throw new Exception("Other thread is syncing!");
            }
            //写入主线程
            locker.AcquireWriterLock(lockTimeout);
            this.m_eventName = name;
            this.m_eventData = data;
            locker.ReleaseWriterLock();
            //等待主线程同步
            try
            {
                while (true)
                {
                    locker.AcquireReaderLock(lockTimeout);
                    if (this.m_eventName == null)
                        break;
                    locker.ReleaseReaderLock();
                }
                return this.m_eventData;
            }
            finally
            {
                locker.ReleaseReaderLock();
                worker.ReleaseSyncing();
            }
        }
        public object CallChild(string name, Package data, bool throwOnError = true)
        {
            if (name == null) return null;
            //获取同步状态
            if (!worker.AcquireSyncing())
            {
                if (!throwOnError) return null;
                throw new Exception("Other thread is syncing!");
            }
            //写入子线程
            locker.AcquireWriterLock(lockTimeout);
            this.c_eventName = name;
            this.c_eventData = data;
            locker.ReleaseWriterLock();
            //等待子线程同步
            try
            {
                while (true)
                {
                    locker.AcquireReaderLock(lockTimeout);
                    if (this.c_eventName == null)
                        break;
                    locker.ReleaseReaderLock();
                }
                return this.c_eventData;
            }
            finally
            {
                locker.ReleaseReaderLock();
                worker.ReleaseSyncing();
            }
        }
        public void ProcessMain()
        {
            if (this.m_eventName != null)
            {
                Func<string, Package, Package> func = this.worker.messageByMain;
                try
                {
                    locker.AcquireWriterLock(lockTimeout);
                    Package data = null;
                    if (this.m_eventName != null && func != null)
                        data = func(this.m_eventName, this.m_eventData);
                    this.m_eventData = data;
                }
                catch (Exception e)
                {
                    this.m_eventData = null;
                    throw e;
                }
                finally
                {
                    this.m_eventName = null;
                    locker.ReleaseWriterLock();
                }
            }
        }
        public void ProcessChild()
        {
            if (this.c_eventName != null)
            {
                Func<string, Package, Package> func = this.worker.messageByChild;
                try
                {
                    locker.AcquireWriterLock(lockTimeout);
                    Package data = null;
                    if (this.c_eventName != null && func != null)
                        data = func(this.c_eventName, this.c_eventData);
                    this.c_eventData = data;
                }
                catch (Exception e)
                {
                    this.c_eventData = null;
                    throw e;
                }
                finally
                {
                    this.c_eventName = null;
                    locker.ReleaseWriterLock();
                }
            }
        }
    }
    public class Package
    {
        /**data type */
        public Type type;
        /**data value */
        public object value;
        /**info */
        public object info;
        /**object id */
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
        /**ArrayBuffer类型为指针传递, 直接传递将多线程共享内存而crash */
        ArrayBuffer,
        RefObject
    }
}

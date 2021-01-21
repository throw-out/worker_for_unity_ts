import * as CS from "csharp";
import { $generic } from "puerts";
let List = $generic(CS.System.Collections.Generic.List$1, CS.System.Object);

const CLOSE_EVENT = "close";

class JsWorker {
    public get isAlive() { return this.worker.IsAlive; }
    public readonly isMain: boolean;
    private readonly worker: CS.JsWorker;
    private readonly callbacks: Map<string, ((data?: any) => void)[]>;
    constructor(loader: CS.Puerts.ILoader | CS.JsWorker) {
        let worker: CS.JsWorker = undefined;
        if (loader instanceof CS.JsWorker) {
            worker = loader;
            this.isMain = false;
        } else {
            worker = CS.JsWorker.New(loader);
            this.isMain = true;
        }
        this.worker = worker;
        this.callbacks = new Map();
        this.working();
    }
    private working() {
        let getValue = (data: CS.JsWorker.Package) => {
            if (data !== undefined && data !== null && data !== void 0) {
                return this.unpackage(data);
            }
            return undefined;
        };
        let onmessage = (name: string, data: CS.JsWorker.Package): CS.JsWorker.Package => {
            let result = undefined;
            let arr = this.callbacks.get(name);
            if (arr) {
                let o = getValue(data);
                for (let cb of arr) {
                    result = cb(o);
                }
            }
            if (result !== undefined && result !== null && result !== void 0)
                return this.package(result);
            return undefined;
        };
        if (this.isMain)
            this.worker.messageByMain = (name, data) => {
                if (name === CLOSE_EVENT) {
                    let o = getValue(data), closing = true;
                    let arr = this.callbacks.get(name);
                    if (arr)
                        arr.forEach(cb => {
                            if ((cb as (data?: any) => boolean)(o) === false)
                                closing = false;
                        });
                    if (closing)
                        this.dispose();
                    return this.package(closing);
                } else
                    return onmessage(name, data);
            };
        else
            this.worker.messageByChild = onmessage;
    }
    private package(data: any, refs?: WeakMap<object, number>, refId?: number): CS.JsWorker.Package {
        refId = refId ?? 1;
        refs = refs ?? new WeakMap();

        let result = new CS.JsWorker.Package();
        let type = typeof (data);
        if ((type === "object" || type === "function") && refs.has(data)) {
            result.type = CS.JsWorker.Type.RefObject;
            result.value = refs.get(data);
        }
        else {
            switch (type) {
                case "object":
                    {
                        //添加引用
                        let id = refId++;
                        result.id = id;
                        refs.set(data, id);
                        //创建C#对象
                        if (data instanceof CS.System.Object) {
                            result.type = CS.JsWorker.Type.Value;
                            result.value = data;
                        }
                        else if (data instanceof ArrayBuffer) {
                            result.type = CS.JsWorker.Type.ArrayBuffer;
                            result.value = CS.JsWorker.Package.ToBytes(data);
                        }
                        else if (Array.isArray(data)) {
                            let list = new List() as CS.System.Collections.Generic.List$1<any>;
                            for (let i = 0; i < data.length; i++) {
                                let item = this.package(data[i], refs, refId);
                                item.info = i;
                                list.Add(item);
                            }
                            result.type = CS.JsWorker.Type.Array;
                            result.value = list;
                        }
                        else {
                            let list = new List() as CS.System.Collections.Generic.List$1<any>;
                            Object.keys(data).forEach(key => {
                                let item = this.package(data[key], refs, refId);
                                item.info = key;
                                list.Add(item);
                            });
                            result.type = CS.JsWorker.Type.Object;
                            result.value = list;
                        }
                    }
                    break;
                case "function":
                    {
                        //添加引用
                        let id = refId++;
                        result.id = id;
                        refs.set(data, id);
                        //创建C#对象
                        result.type = CS.JsWorker.Type.Function;
                        result.value = data.toString();
                    }
                    break;
                case "string":
                case "number":
                case "bigint":
                case "boolean":
                    result.type = CS.JsWorker.Type.Value;
                    result.value = data;
                    break;
                default:
                    result.type = CS.JsWorker.Type.Unknown;
                    break;
            }
        }
        return result;
    }
    private unpackage(data: CS.JsWorker.Package, refs?: Map<number, Object>): any {
        refs = refs ?? new Map();
        let result = undefined, id = data.id, value = data.value;
        switch (data.type) {
            case CS.JsWorker.Type.Object:
                {
                    result = {};
                    if (id > 0) refs.set(id, result); //Add ref object
                    let arr = value as CS.System.Collections.Generic.List$1<CS.JsWorker.Package>;
                    for (let i = 0; i < arr.Count; i++) {
                        let item = arr.get_Item(i);
                        result[item.info] = this.unpackage(item, refs);
                    }
                }
                break;
            case CS.JsWorker.Type.Array:
                {
                    result = [];
                    if (id > 0) refs.set(id, result); //Add ref object
                    let arr = value as CS.System.Collections.Generic.List$1<CS.JsWorker.Package>;
                    for (let i = 0; i < arr.Count; i++) {
                        let item = arr.get_Item(i);
                        result[item.info] = this.unpackage(item, refs);
                    }
                }
                break;
            case CS.JsWorker.Type.ArrayBuffer:
                result = CS.JsWorker.Package.ToArrayBuffer(value);
                if (id > 0) refs.set(id, result); //Add ref object
                break;
            case CS.JsWorker.Type.Function:
                result = eval(value);
                if (id > 0) refs.set(id, result); //Add ref object
                break;
            case CS.JsWorker.Type.RefObject:
                if (refs.has(value))
                    result = refs.get(value);
                else
                    result = "Error: ref id " + value + " not found";
                break;
            case CS.JsWorker.Type.Unknown:
            default:
                result = value;
                if (id > 0) refs.set(id, result); //Add ref object
                break;
        }
        return result;
    }
    public start(filepath: string) {
        if (globalWorker && globalWorker["worker"] == this.worker)
            throw new Error("Thread cannot called start");

        this.worker.Startup(filepath);
    }
    public dispose() {
        if (globalWorker && globalWorker["worker"] == this.worker)
            this.post(CLOSE_EVENT);
        else {
            this.worker.Dispose();
            this.callbacks.clear();
        }
    }
    public post(eventName: string, data?: any) {
        let o: CS.JsWorker.Package;
        if (data !== undefined && data !== null && data !== void 0) {
            o = this.package(data);
        }
        if (this.isMain)
            this.worker.CallChild(eventName, o);
        else
            this.worker.CallMain(eventName, o);
    }
    public postSync<T>(eventName: string, data?: any): T {
        let o: CS.JsWorker.Package, result = undefined;
        if (data !== undefined && data !== null && data !== void 0) {
            o = this.package(data);
        }
        if (this.isMain)
            result = this.worker.Sync.CallChild(eventName, o);
        else
            result = this.worker.Sync.CallMain(eventName, o);
        //Result
        if (result !== undefined && result !== null && result !== void 0) {
            result = this.unpackage(result);
        }
        return result;
    }
    public eval(chunk: string, chunkName?: string) {
        if (globalWorker && globalWorker["worker"] == this.worker)
            throw new Error("Thread cannot called eval");

        this.worker.Eval(chunk, chunkName);
    }
    public on(eventName: string, cb: (data?: any) => void) {
        if (eventName && cb) {
            let arr = this.callbacks.get(eventName);
            if (!arr) {
                arr = [];
                this.callbacks.set(eventName, arr);
            }
            arr.push(cb);
        }
    }
    public remove(eventName: string, cb: (data?: any) => void) {
        let arr = this.callbacks.get(eventName);
        if (arr) {
            let index = arr.indexOf(cb);
            if (index >= 0)
                this.callbacks.set(eventName, [...arr.slice(0, index), ...arr.slice(index + 1)]);
        }
    }
    public removeAll(eventName?: string) {
        if (eventName)
            this.callbacks.delete(eventName);
        else
            this.callbacks.clear();
    }
}
(function () {
    let _this = (this ?? globalThis);
    _this["JsWorker"] = JsWorker;
    _this["globalWorker"] = undefined;
})();

/**
 * 接口声明
 */
declare global {
    class JsWorker {
        public get isAlive(): boolean;
        public readonly isMain: boolean;
        public constructor(loader: CS.Puerts.ILoader);
        /**
         * 开始执行脚本(实例生命周期内仅调用一次) 
         */
        public start(filepath: string): void;
        /**
         * 关闭JsWorker实例, 不可在内部关闭实例
         */
        public dispose(): void;
        /**
         * 发送一条消息(异步)
         */
        public post(eventName: string, data?: any): void;
        /**
         * 同步发送消息并获取返回值
         */
        public postSync<T>(eventName: string, data?: any): T;
        /**
         * 执行一段代码, 由外部程序调用
         */
        public eval(chunk: string, chunkName?: string): void;
        /**
         * 监听事件信息
         */
        public on(eventName: string, cb: (data?: any) => void): void;
        /**
         * 监听并劫持JsWorker实例close消息
         */
        public on(eventName: "close", cb: (state?: any) => boolean): void;
        /**
         * 移除一条监听
         */
        public remove(eventName: string, cb: (data?: any) => void): void;
        /**
         * 移除所有监听
         */
        public removeAll(eventName: string): void;
        /**
         * 移除所有监听
         */
        public removeAll(): void;
    }
    /**
     * 只能在JsWorker线程内部访问, 与主线程交互的对象
     */
    const globalWorker: JsWorker;
}

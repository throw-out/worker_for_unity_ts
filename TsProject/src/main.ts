import * as CS from "csharp";
import { $extension, $generic } from "puerts";

//加载工具集
(function () {
    require("./common/jsWorker");
})();


const id = CS.System.Threading.Thread.CurrentThread.ManagedThreadId;
const threadName = `Main(${id})\t`;

let worker = new JsWorker(CS.JsManager.GetInstance().Loader);
worker.on("main_on", () => "this is main thread");
worker.on("data", data => {
    if (typeof data == "function") {
        console.log(threadName, data.toString());
    }
    else if (typeof data == "object") {
        console.log(threadName, JSON.stringify(data));
    }
    else
        console.log(threadName, data);
});

worker.start("./test");

worker.post("data", { msg: "this main thread message" });
//JsWorker在JsEnv初始化时需要主线程调用, 所以此处不能调用阻塞方法
setTimeout(() => {
    console.log(threadName, worker.postSync<string>("child_on"))
}, 1000);

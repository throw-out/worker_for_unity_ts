import * as CS from "csharp";

const id = CS.System.Threading.Thread.CurrentThread.ManagedThreadId;
const threadName = `Child(${id})\t`;

globalWorker.on("child_on", () => "this is child thread");
globalWorker.on("data", data => {
    if (typeof data == "function") {
        console.log(threadName, data.toString());
    }
    else if (typeof data == "object") {
        console.log(threadName, JSON.stringify(data));
    }
    else
        console.log(threadName, data);
});

globalWorker.post("data", { msg: "this child thread message" });
console.log(threadName, globalWorker.postSync("main_on"));

setTimeout(() => {
    let i = 3;
    while (i-- > 0) {
        globalWorker.post("data", { msg: "this is child thread message", index: i });
        CS.System.Threading.Thread.Sleep(1000);
    }
}, 1000);
module Tests.Scheduler
open Kernel
open Tests.Kernel

let makeTaskWithStatus id status deps =
    { makeTask id deps with status = status }

open Expecto

[<Tests>]
let schedulerTickTests =
    testList "Scheduler.tick" [
        testCase "no deps merged → only a starts (b waits)" <| fun _ ->
            let a = makeTaskWithStatus (TaskId "a") TaskStatus.Pending []
            let b = makeTaskWithStatus (TaskId "b") TaskStatus.Pending [TaskId "a"]
            let dag = { sessionId = ""; tasks = Map.ofList [(TaskId "a", a); (TaskId "b", b)]; rootRequirement = "" }
            let out = tick { dag = dag; config = { maxConcurrent = 3; masterBranch = "main"; terminal = "alacritty"; sharedDirs = [] } }
            Expect.equal out.toStart.Length 1 "a (no deps) should start, b waits"

        testCase "independent task → toStart" <| fun _ ->
            let a = makeTaskWithStatus (TaskId "a") TaskStatus.Pending []
            let dag = { sessionId = ""; tasks = Map.ofList [(TaskId "a", a)]; rootRequirement = "" }
            let out = tick { dag = dag; config = { maxConcurrent = 3; masterBranch = "main"; terminal = "alacritty"; sharedDirs = [] } }
            Expect.equal out.toStart.Length 1 "a should start"

        testCase "maxConcurrent limit" <| fun _ ->
            let a = makeTaskWithStatus (TaskId "a") TaskStatus.Pending []
            let b = makeTaskWithStatus (TaskId "b") TaskStatus.Pending []
            let c = makeTaskWithStatus (TaskId "c") TaskStatus.Pending []
            let dag = { sessionId = ""; tasks = Map.ofList [(TaskId "a", a); (TaskId "b", b); (TaskId "c", c)]; rootRequirement = "" }
            let out = tick { dag = dag; config = { maxConcurrent = 2; masterBranch = "main"; terminal = "alacritty"; sharedDirs = [] } }
            Expect.equal out.toStart.Length 2 "only 2 should start"

        testCase "dependency not merged → only a starts (b waits)" <| fun _ ->
            let a = makeTaskWithStatus (TaskId "a") TaskStatus.Pending []
            let b = makeTaskWithStatus (TaskId "b") TaskStatus.Pending [TaskId "a"]
            let dag = { sessionId = ""; tasks = Map.ofList [(TaskId "a", a); (TaskId "b", b)]; rootRequirement = "" }
            let out = tick { dag = dag; config = { maxConcurrent = 3; masterBranch = "main"; terminal = "alacritty"; sharedDirs = [] } }
            Expect.equal out.toStart.Length 1 "a (no deps) should start, b waits for merged a"

        testCase "merged dependency → ready" <| fun _ ->
            let a = makeTaskWithStatus (TaskId "a") (TaskStatus.Merged "abc") []
            let b = makeTaskWithStatus (TaskId "b") TaskStatus.Pending [TaskId "a"]
            let dag = { sessionId = ""; tasks = Map.ofList [(TaskId "a", a); (TaskId "b", b)]; rootRequirement = "" }
            let out = tick { dag = dag; config = { maxConcurrent = 3; masterBranch = "main"; terminal = "alacritty"; sharedDirs = [] } }
            Expect.equal out.toStart.Length 1 "b ready after a merged"
            Expect.equal out.events.Length 1 "one event emitted"

        testCase "Running task not counted as ready" <| fun _ ->
            let a = makeTaskWithStatus (TaskId "a") TaskStatus.Running []
            let b = makeTaskWithStatus (TaskId "b") TaskStatus.Pending [TaskId "a"]
            let dag = { sessionId = ""; tasks = Map.ofList [(TaskId "a", a); (TaskId "b", b)]; rootRequirement = "" }
            let out = tick { dag = dag; config = { maxConcurrent = 3; masterBranch = "main"; terminal = "alacritty"; sharedDirs = [] } }
            Expect.equal out.toStart [] "b waits for a (running != merged)"
    ]

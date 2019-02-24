namespace Ionide.VSCode.Helpers

open System
open Fable.Core

module JS =

    [<Emit("($0 != undefined)")>]
    let isDefined (o: obj) : bool = failwith "never"

//---------------------------------------------------
//PromisesExt (by Dave)
//---------------------------------------------------
module Promise =
    open System
    open Fable.Core
    open Fable.Import
    open Fable.Import.JS

    /// <summary>
    /// Creates promise (in rejected state) with supplied reason.
    /// </summary>
    let reject<'T> reason : Promise<'T> =
        Promise.reject<'T> reason

    /// <summary>
    /// Allows handing promise which is in fulfilled state.
    /// Can be used for side-effects.
    /// </summary>
    let onSuccess (a : 'T -> unit) (pr : Promise<'T>) : Promise<'T> =
        pr.``then``(
            (fun value -> a value; value)
        )

    /// <summary>
    /// Allows handing promise which is in rejected state. Propagates rejected promise, to allow chaining.
    /// Can be used for side-effects.
    /// </summary>
    let onFail (a : obj -> unit) (pr : JS.Promise<'T>) : JS.Promise<'T> =
        pr.catch (fun reason -> a reason |> ignore; (Promise.reject reason) |> unbox)

//---------------------------------------------------
//VS Code Helpers
//---------------------------------------------------
module VSCode =
    open Fable.Import.vscode

    let getPluginPath pluginName =
        let ext = extensions.getExtension pluginName
        ext.extensionPath


//---------------------------------------------------
//Process Helpers
//---------------------------------------------------
module Process =
    let splitArgs cmd =
        if cmd = "" then
            [||]
        else
            cmd.Split(' ')
            |> Array.toList
            |> List.fold (fun (quoted : string option,acc) e ->
                if quoted.IsSome then
                    if e.EndsWith "\"" then None, (quoted.Value + " " + e).Replace("\"", "")::acc
                    else Some (quoted.Value + " " + e), acc
                else
                    if e.StartsWith "\"" &&  e.EndsWith "\"" then None, e.Replace("\"", "")::acc
                    elif e.StartsWith "\"" then Some e, acc
                    elif String.IsNullOrEmpty e then None, acc
                    else None, e::acc
            ) (None,[])
            |> snd
            |> List.rev
            |> List.toArray

    open Fable.Import.JS
    open Fable.Import.Node
    open Fable.Import.Node.ChildProcess
    open Fable.Import.vscode
    open Fable.Core.JsInterop

    module node = Fable.Import.Node.Exports

    [<Emit("process.platform")>]
    let private platform: string = jsNative

    let isWin () = platform = "win32"
    let isMono () = platform <> "win32"

    let onExit (f : int option -> string option -> unit) (proc : ChildProcess) =
        proc.on("exit", f |> unbox<obj -> unit>) |> ignore
        proc

    let onOutput (f : Buffer.Buffer -> _) (proc : ChildProcess) =
        proc.stdout?on $ ("data", f |> unbox) |> ignore
        proc

    let onErrorOutput (f : Buffer.Buffer -> _) (proc : ChildProcess) =
        proc.stderr?on $ ("data", f |> unbox) |> ignore
        proc

    let onError (f: obj -> _) (proc : ChildProcess) =
        proc?on $ ("error", f |> unbox) |> ignore
        proc

    let spawn location linuxCmd (cmd : string) =
        let cmd' = splitArgs cmd |> ResizeArray

        let options =
            createObj [
                "cwd" ==> workspace.rootPath
            ]
        if isWin () || linuxCmd = "" then
            node.childProcess.spawn(location, cmd', options)
        else
            let prms = seq { yield location; yield! cmd'} |> ResizeArray
            node.childProcess.spawn(linuxCmd, prms, options)

    let spawnInDir location linuxCmd (cmd : string) =
        let cmd' = splitArgs cmd |> ResizeArray

        let options =
            createObj [
                "cwd" ==> (node.path.dirname location)
            ]
        if isWin () || linuxCmd = "" then

            node.childProcess.spawn(location, cmd', options)
        else
            let prms = seq { yield location; yield! cmd'} |> ResizeArray
            node.childProcess.spawn(linuxCmd, prms, options)

    let spawnWithShell location linuxCmd (cmd : string) =
        let cmd' =
            if isWin() then
                splitArgs cmd
                |> Seq.map (fun c -> if c.Contains " " then sprintf "\"%s\"" c else c )
                |> Seq.toArray
            else
                splitArgs cmd
                |> Seq.toArray

        if isWin () || linuxCmd = "" then
            window.createTerminal("F# Application", location, cmd')
        else
            let prms = seq { yield location; yield! cmd'} |> Seq.toArray
            window.createTerminal("F# Application", linuxCmd, prms)


    let spawnWithNotification location linuxCmd (cmd : string) (outputChannel : OutputChannel) =
        spawn location linuxCmd cmd
        |> onOutput(fun e -> e.toString () |> outputChannel.append)
        |> onError (fun e -> e.ToString () |> outputChannel.append)
        |> onErrorOutput(fun e -> e.toString () |> outputChannel.append)

    let spawnWithNotificationInDir location linuxCmd (cmd : string) (outputChannel : OutputChannel) =
        spawnInDir location linuxCmd cmd
        |> onOutput(fun e -> e.toString () |> outputChannel.append)
        |> onError (fun e -> e.ToString () |> outputChannel.append)
        |> onErrorOutput(fun e -> e.toString () |> outputChannel.append)

    type ChildProcessExit = {
        Code: int option
        Signal: string option
    }

    let toPromise (proc : ChildProcess) =
        Promise.Create<ChildProcessExit>(fun resolve _error ->
            proc
            |> onExit(fun code signal -> { Code = code; Signal = signal } |> resolve )
            |> ignore
        )

    let exec location linuxCmd cmd : Promise<ExecError option * string * string> =
        let options = createEmpty<ExecOptions>
        options.cwd <- Some workspace.rootPath

        Promise.Create<ExecError option * string * string>(fun resolve error ->
            let execCmd =
                if isWin () then location + " " + cmd
                else linuxCmd + " " + location + " " + cmd
            node.childProcess.exec(execCmd, options,
                (fun (e : ExecError option) (i : U2<string, Buffer.Buffer>) (o : U2<string, Buffer.Buffer>) ->
                    // As we don't specify an encoding, the documentation specifies that we'll receive strings
                    // "By default, Node.js will decode the output as UTF-8 and pass strings to the callback"
                    let arg = e, unbox<string> i, unbox<string> o
                    resolve arg)) |> ignore)


//---------------------------------------------------
//Settings Helpers
//---------------------------------------------------
module Settings =
    open Fable.Import.vscode
    open Fable.Import.Node
    module node =  Fable.Import.Node.Exports

    module Toml =
        [<Emit("toml.parse($0)")>]
        let parse (str : string) : 'a = failwith "JS"

    type FakeSettings = {
        linuxPrefix : string
        command : string
        build : string
        parameters : string []
        test : string
    }

    type WebPreviewSettings = {
        linuxPrefix : string
        command : string
        host : string
        port : int
        script : string
        build : string
        startString : string
        parameters : string []
        startingPage : string
    }

    type Settings = {
        Fake : FakeSettings
        WebPreview : WebPreviewSettings
    }

    let loadOrDefault<'a> (map : Settings -> 'a)  (def :'a) =
        try
            let path = workspace.rootPath + "/.ionide"
            let t = node.fs.readFileSync(path).toString ()
                    |> Toml.parse
                    |> map
            if JS.isDefined t then t else def
        with
        | _ -> def

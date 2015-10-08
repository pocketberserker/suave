﻿module Suave.LibUv.Tcp

#nowarn "9"

open System
open System.Runtime.InteropServices

open Native

type Handle = IntPtr

let createHandle size : Handle =
  Marshal.AllocCoTaskMem(size)

let destroyHandle (handle : Handle) =
  Marshal.FreeCoTaskMem handle

open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open System.Collections.Generic

type SingleThreadSynchronizationContext(loop) =

  let queue = new ConcurrentQueue<KeyValuePair<SendOrPostCallback, obj>>()

  let synchronizationContextCallback = createHandle <| uv_handle_size(uv_handle_type.UV_TCP)

  [<DefaultValue>] val mutable uv_handle_cb : uv_handle_cb
 
  member this.Post( d : SendOrPostCallback, state : obj) =
    if (d = null) then
      raise( ArgumentNullException("d"))
    else queue.Enqueue(new KeyValuePair<SendOrPostCallback, obj>(d, state))

  member this.Send() =
    uv_async_send(synchronizationContextCallback) |> ignore

  member this.RunOnCurrentThread( _ :IntPtr)  =
      let mutable workItem = KeyValuePair(null,null)
      while queue.TryDequeue( &workItem) do
        workItem.Key.Invoke(workItem.Value)

  member this.Init() =
    this.uv_handle_cb <- uv_handle_cb(this.RunOnCurrentThread)
    uv_async_init(loop,synchronizationContextCallback,this.uv_handle_cb) |> ignore

open Suave.Sockets
open Suave.Utils.Async

type ReadOp() =

  [<DefaultValue>] val mutable buf : ArraySegment<byte>
  [<DefaultValue>] val mutable ok : Choice<int,Error> -> unit
  [<DefaultValue>] val mutable uv_alloc_cb : uv_alloc_cb
  [<DefaultValue>] val mutable uv_read_cb : uv_read_cb

  member this.end_read ((client : IntPtr), (nread : int), (buff : byref<uv_buf_t>)) =

    let h = uv_read_stop(client)

    if (nread < 0) then
      if (nread <> UV_EOF) then
        let err = new string(uv_err_name(nread))
        this.ok (Choice2Of2 <| ConnectionError err)
      else
        this.ok (Choice2Of2 <| ConnectionError("eof"))
    elif (nread > 0) then
      this.ok (Choice1Of2 nread)

  member this.alloc_buffer ((_ : IntPtr), (suggested_size: int), ([<Out>] buff : byref<uv_buf_t>)) =
    buff.``base`` <- Marshal.UnsafeAddrOfPinnedArrayElement(this.buf.Array,this.buf.Offset)
    buff.len <- this.buf.Count

  member this.uv_read_start cn buf ok =
    this.buf <- buf
    this.ok <- ok
    uv_read_start(cn, this.uv_alloc_cb, this.uv_read_cb) |> ignore

  member this.Initialize() =
    this.uv_alloc_cb <- uv_alloc_cb(fun a b c -> this.alloc_buffer(a, b, &c))
    this.uv_read_cb <- uv_read_cb(fun a b c -> this.end_read (a, b, &c))

type WriteOp() =

  [<DefaultValue>] val mutable wrbuffArray : uv_buf_t[]
  [<DefaultValue>] val mutable uv_write_cb : uv_write_cb
  [<DefaultValue>] val mutable ok : Choice<unit,Error> -> unit

  member this.end_write (request : IntPtr) (status : int) =
    Marshal.FreeCoTaskMem request
    if (status < 0) then 
      let err = new string(uv_strerror(status))
      this.ok (Choice2Of2 <| ConnectionError err)
    else
      this.ok (Choice1Of2())

  member this.Initialize() =
    this.wrbuffArray <- [| new uv_buf_t() |]
    this.uv_write_cb <- uv_write_cb(this.end_write)

  member this.uv_write cn (buf : ArraySegment<byte>) ok =
    this.ok <- ok
    this.wrbuffArray.[0].``base`` <- Marshal.UnsafeAddrOfPinnedArrayElement(buf.Array,buf.Offset)
    this.wrbuffArray.[0].len <- buf.Count
    let request = Marshal.AllocCoTaskMem(uv_req_size(uv_request_type.UV_WRITE))
    uv_write(request, cn, this.wrbuffArray, 1, this.uv_write_cb )
    |> ignore

type OperationPair = ReadOp*WriteOp

open Suave.Logging

type LibUvTransport(pool : ConcurrentPool<OperationPair>,loop : IntPtr,client : Handle, synchronizationContext : SingleThreadSynchronizationContext,logger : Logger) =

  [<DefaultValue>] val mutable uv_close_cb : uv_close_cb
  [<DefaultValue>] val mutable pin : GCHandle
  [<DefaultValue>] val mutable cont : unit -> unit

  let closeEvent = new ManualResetEvent(false)

  let (readOp,writeOp) = pool.Pop()
  let intern = Suave.Log.intern logger "Suave.Tcp.job"
 
  member this.uv_close_callback _ =
    intern "Entering uv_close callback."
    destroyHandle client
    pool.Push (readOp,writeOp)
    this.pin.Free()
    closeEvent.Set() |> ignore
    this.cont ()

  member this.uv_close cont =
    this.cont <- cont
    intern "Calling uv_close."
    uv_close(client, this.uv_close_cb) |> ignore

  member this.initialize() =
    this.pin <- GCHandle.Alloc(this,GCHandleType.Normal)
    this.uv_close_cb <- uv_close_cb(this.uv_close_callback)

  member this.shutdown() =
    Async.FromContinuations <| fun (ok, _, _) ->
      synchronizationContext.Post(SendOrPostCallback(fun o -> this.uv_close ok),null)
      synchronizationContext.Send()

  interface ITransport with
    member this.read (buf : ByteSegment) =
      Async.FromContinuations <| fun (ok, _, _) ->
        synchronizationContext.Post(SendOrPostCallback(fun o -> readOp.uv_read_start client buf ok),null)
        synchronizationContext.Send()

    member this.write(buf : ByteSegment) =
      Async.FromContinuations <| fun (ok, _, _) ->
      synchronizationContext.Post(SendOrPostCallback(fun o -> writeOp.uv_write client buf ok),null)
      synchronizationContext.Send()

let createLibUvOpsPool maxOps =

  let opsPool = new ConcurrentPool<OperationPair>()

  for x = 0 to maxOps - 1 do

    let readOp = new ReadOp()
    let writeOp = new WriteOp()
    readOp.Initialize()
    writeOp.Initialize()
    opsPool.Push (readOp,writeOp)

  opsPool

open System.Runtime.InteropServices

/// The max backlog of number of requests
[<Literal>]
let MaxBacklog = Int32.MaxValue

let private aFewTimes f =
  let s ms = System.Threading.Thread.Sleep (ms : int)
  let rec run = function
    | 0us -> f ()
    | n -> try f () with e -> s 20000; run (n - 1us)
  run 5us

let bindSocket server bindCallback=
  let r = uv_listen(server, MaxBacklog, bindCallback)
  if r<>0 then
    failwith ("Listen error: " + (new string(uv_strerror(r))))

open Suave.Logging
open Suave.Types
open Suave.Tcp
open Suave

type LibUvSocket(pool : ConcurrentPool<OperationPair>,logger, serveClient, ip, loop , bufferManager, startData, acceptingConnections: AsyncResultCell<StartedData>) =

  [<DefaultValue>] val mutable uv_connection_cb : uv_connection_cb
  [<DefaultValue>] val mutable synchronizationContext : SingleThreadSynchronizationContext

  member this.on_new_connection (server : IntPtr) (status: int) =

    if status < 0 then
          "New connection error: " +  (new string (uv_strerror(status))) |> Log.intern logger "Suave.Tcp.LibUvSocket.on_new_connection"
    else

      let client = createHandle <| uv_handle_size(uv_handle_type.UV_TCP)

      let _ = uv_tcp_init(loop, client)

      if (uv_accept(server, client) = 0) then
        let transport = new LibUvTransport(pool,loop,client,this.synchronizationContext,logger)
        transport.initialize()
        Async.Start <| 
            job logger serveClient ip transport bufferManager (transport.shutdown())
      else
        destroyHandle client

  member this.Initialize() =
    this.uv_connection_cb <- uv_connection_cb(this.on_new_connection)
    this.synchronizationContext <- new SingleThreadSynchronizationContext(loop)
    this.synchronizationContext.Init()

  member this.run(server) =

    aFewTimes(fun () -> bindSocket server this.uv_connection_cb)

    let startData = { startData with socketBoundUtc = Some (Globals.utcNow()) }
    acceptingConnections.Complete startData |> ignore

    logger.Log LogLevel.Info <| fun _ ->
        { path          = "Suave.Tcp.tcpIpServer"
          trace         = TraceHeader.empty
          message       = "listener started in " + (startData.ToString())
          level         = LogLevel.Info
          ``exception`` = None
          tsUTCTicks    = Globals.utcNow().Ticks }

    let _ = uv_run(loop, UV_RUN_DEFAULT)
    ignore()

  member this.exit() =
    this.uv_connection_cb <- null

type LibUvServer(maxConcurrentOps,bufferManager,logger : Logger, binding, startData, serveClient, acceptingConnections: AsyncResultCell<StartedData>, event : ManualResetEvent) =

  [<DefaultValue>] val mutable thread : Thread
  [<DefaultValue>] val mutable uv_async_stop_loop_cb : uv_handle_cb

  let mutable addr = sockaddr_in( a = 0L, b= 0L, c = 0L, d = 0L)

  let loop = createHandle <| uv_loop_size()

  let server = createHandle <| uv_handle_size(uv_handle_type.UV_TCP)

  let ip = binding.ip.ToString()
  let port = int binding.port

  let stopLoopCallback = createHandle <| uv_handle_size(uv_handle_type.UV_ASYNC)

  let closeEvent = new ManualResetEvent(false)

  let opsPool = createLibUvOpsPool maxConcurrentOps

  member this.run () =
    Thread.BeginThreadAffinity()
    try
      let _ = uv_tcp_init(loop, server)
      let _ = uv_ip4_addr(ip, port, &addr)
      let _ = uv_tcp_bind(server, &addr, 0)
      let s = new LibUvSocket(opsPool,logger, serveClient, binding.ip, loop, bufferManager, startData, acceptingConnections)
      s.Initialize()
      s.run(server)
      s.exit()
    with ex ->
      Log.infoe logger "Suave.Tcp.runServerLibUv" TraceHeader.empty ex "could not start LibUvSocket"
    Log.info logger "LibUvServer.run" TraceHeader.empty "exiting server."
    this.destroy()
    event.Set() |> ignore

  member this.uv_stop_loop (_ : IntPtr) =
    uv_stop(loop) |> ignore
    closeEvent.Set() |> ignore

  member this.init() = 
    this.thread <- new Thread(this.run)
    this.uv_async_stop_loop_cb <- uv_handle_cb(this.uv_stop_loop)
    let _ = uv_loop_init(loop)
    uv_async_init(loop, stopLoopCallback, this.uv_async_stop_loop_cb) |> ignore

  member this.start() = 
    this.thread.Start()

  member this.stopLoop() =
    uv_async_send (stopLoopCallback) |> ignore
    closeEvent.WaitOne() |> ignore

  member this.destroy() =

    uv_loop_close(loop) |> ignore
    uv_close(server,null)
    uv_close(stopLoopCallback, null)
    destroyHandle loop
    destroyHandle server
    destroyHandle stopLoopCallback

let runServerLibUv logger maxConcurrentOps bufferSize (binding: SocketBinding) startData (acceptingConnections: AsyncResultCell<StartedData>) serveClient =

  let bufferManager = new BufferManager(bufferSize * (maxConcurrentOps + 1), bufferSize, logger)
  bufferManager.Init()

  let syncEvent = new ManualResetEvent(false)

  let libUvServer = new LibUvServer(maxConcurrentOps,bufferManager, logger, binding, startData, serveClient, acceptingConnections, syncEvent)

  libUvServer.init()

  async{
    use! disposable = Async.OnCancel(fun () -> libUvServer.stopLoop())
    do libUvServer.start()
    let! _ = Async.AwaitWaitHandle (syncEvent)
    return ()
  }

type LibUvServerFactory() =
  interface TcpServerFactory with
    member this.create (logger,maxOps, bufferSize,binding) =
      runServerLibUv logger maxOps bufferSize binding.socketBinding
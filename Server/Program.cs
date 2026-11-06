using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using BlockCircuit;

if(args.Contains("--self-test")){SelfTests.Run();return;}
if(args.Contains("--multiplayer-tests")){MultiplayerTests.Run();return;}
if(args.Contains("--help")){
    Console.WriteLine("BlockCircuit dedicated server: --bind 127.0.0.1 --port 25565 --world data/world.json --seed 2026 --run-seconds 0\nPassword: BLOCKCIRCUIT_SERVER_PASSWORD environment variable. Ctrl+C / SIGTERM saves and stops.");return;
}
try{
    string Option(string key,string fallback){int i=Array.IndexOf(args,key);return i<0?fallback:i+1<args.Length?args[i+1]:throw new ArgumentException("Missing value for "+key);}
    string[] allowed=["--bind","--port","--world","--seed","--run-seconds"];
    for(int i=0;i<args.Length;i+=2)if(!allowed.Contains(args[i])||i+1>=args.Length)throw new ArgumentException("Unknown or incomplete option: "+args[i]);
    var address=IPAddress.Parse(Option("--bind","127.0.0.1"));
    int port=int.Parse(Option("--port","25565")),seed=int.Parse(Option("--seed","2026"));
    double duration=double.Parse(Option("--run-seconds","0"),System.Globalization.CultureInfo.InvariantCulture);
    if(port is <1 or >65535||!double.IsFinite(duration)||duration<0)throw new ArgumentException("Invalid port or duration.");
    string password=Environment.GetEnvironmentVariable("BLOCKCIRCUIT_SERVER_PASSWORD")??"";
    string path=Path.GetFullPath(Option("--world","data/world.json"));Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var lease=new FileStream(path+".server.lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    var world=World.CreateStreaming(seed);var position=world.Spawn();float yaw=0,pitch=0;
    if(File.Exists(path)){var saved=world.Load(path);position=saved.Position;yaw=saved.Yaw;pitch=saved.Pitch;}
    using var streamer=new WorldStreamer(world,path,new StreamingOptions{MaxResidentColumns=512});
    using var host=new MultiplayerHost(world,port,password,"Server",address,dedicated:true);
    world.FallingBlocks.CanLand=cell=>!host.AnyPlayerOverlaps(cell);
    using var stop=new CancellationTokenSource();
    ConsoleCancelEventHandler cancel=(_,e)=>{e.Cancel=true;stop.Cancel();};Console.CancelKeyPress+=cancel;
    using var term=OperatingSystem.IsWindows()?null:PosixSignalRegistration.Create(PosixSignal.SIGTERM,c=>{c.Cancel=true;stop.Cancel();});
    var timer=Stopwatch.StartNew();var clock=new GameClock();double previous=0,nextSave=60;
    Console.WriteLine($"READY TCP {address}:{host.Port}; 20 TPS; world={path}; guests<=8");
    try{
        while(!stop.IsCancellationRequested&&(duration==0||timer.Elapsed.TotalSeconds<duration)){
            double now=timer.Elapsed.TotalSeconds;
            streamer.Update(position,2,yaw,pitch,host.InterestPositions);
            host.Pump(new(position,yaw,pitch));
            clock.Advance(now-previous,_=>{world.Tick();world.Circuits.ProcessPending();});previous=now;
            if(streamer.LastError is {} error)throw new IOException("Terrain streaming failed.",error);
            if(host.Error is {} networkError)throw new IOException(networkError);
            if(now>=nextSave){streamer.FlushSaves(position,yaw,pitch);nextSave=now+60;Console.WriteLine("World saved.");}
            Thread.Sleep(5);
        }
    }finally{
        host.Dispose(); // Persist all remote inventories before the final world save.
        streamer.FlushSaves(position,yaw,pitch);Console.CancelKeyPress-=cancel;
        Console.WriteLine($"STOPPED; saved world; ticks={world.TickCount}");
    }
}catch(Exception e){Console.Error.WriteLine("Server failed: "+e.Message);Environment.ExitCode=1;}

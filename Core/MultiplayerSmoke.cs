using System.Net;
using System.Numerics;
using System.Text.Json;
using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    public void RunMultiplayerSmoke()
    {
        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow|ConfigFlags.Msaa4xHint);
        Raylib.InitWindow(1280,800,"Block Circuit | multiplayer validation");Raylib.SetWindowMinSize(960,640);Raylib.SetTargetFPS(60);Raylib.SetExitKey(KeyboardKey.Null);
        renderer=new BlockRenderer();
        world=new World(false);world.EnableSlotInventory();
        for(int x=0;x<40;x++)for(int z=0;z<40;z++)world.Set(new(x,0,z),Block.Stone);
        feet=new(20.5f,1.01f,23.5f);previousFeet=feet;yaw=MathF.PI;pitch=-.25f;
        multiplayerHost=new(world,0,"smoke","Host",IPAddress.Loopback);
        var guest=new Game(smoke:true);guest.renderer=new BlockRenderer();
        var originalSolo=guest.world;var originalBlocks=originalSolo.Blocks;var originalInventory=originalSolo.Inventory.ToArray();
        guest.soloWorld=guest.world;guest.soloFeet=guest.feet;guest.soloYaw=guest.yaw;guest.soloPitch=guest.pitch;guest.soloSlot=guest.palette.SelectedSlot;
        guest.multiplayerClient=new(NetPeer.ConnectAsync("127.0.0.1",multiplayerHost.Port).GetAwaiter().GetResult(),"Builder","smoke",Guid.NewGuid(),world.Registries);
        using var spectator=new MultiplayerClient(NetPeer.ConnectAsync("127.0.0.1",multiplayerHost.Port).GetAwaiter().GetResult(),"Explorer","smoke",Guid.NewGuid(),world.Registries);
        bool placed=false,replicated=false,crafted=false,restored=false;int menuTicks=0;int stage=0,stageFrame=0,frames=0;Cell target=default;
        var watch=System.Diagnostics.Stopwatch.StartNew();
        string? failure=null;
        try{
            while(frames<260&&watch.Elapsed<TimeSpan.FromSeconds(15)){
                if(frames%3==0){if(multiplayerOpen){IdleNetworkTick(1f/20);menuTicks++;}else world.Tick();if(guest.NetworkClientActive){if(guest.inventoryOpen)guest.IdleNetworkTick(1f/20);else guest.SimulationTick(1f/20);}}
                PumpMultiplayer();guest.PumpMultiplayer();
                spectator.Pump(spectator.LocalPose??LocalNetworkPose());
                if(guest.multiplayerClient?.Error is {} error)throw new InvalidOperationException("Guest failed: "+error);
                if(spectator.Error is {} other)throw new InvalidOperationException("Spectator failed: "+other);
                if(guest.NetworkClientActive&&spectator.Ready){
                    switch(stage){
                        case 0:
                            var position=guest.feet;target=new((int)MathF.Floor(position.X),1,(int)MathF.Floor(position.Z)-2);
                            guest.actions.Enqueue(new(ActionKind.Place,target,Block.Grass,0));guest.SimulationTick(1f/20);
                            feet=new(20.5f,3.5f,29.5f);previousFeet=feet;player.Flying=true;
                            stage=1;stageFrame=frames;break;
                        case 1:
                            if(world.Get(target)==Block.Grass&&guest.world.Get(target)==Block.Grass&&spectator.World!.Get(target)==Block.Grass){
                                placed=true;replicated=true;guest.PickBlock(Block.OakLog);stage=2;stageFrame=frames;
                            }break;
                        case 2:
                            if(guest.multiplayerClient!.PendingCommands==0&&guest.world.Items!.Slots[0]?.Id==new ContentId("blockcircuit:oak_log")){
                                guest.inventoryGesture.Begin(guest.world.Items,new(ItemArea.Storage,0),false,false);
                                guest.inventoryGesture.Release(guest.world.Items);
                                guest.inventoryGesture.Begin(guest.world.Items,new(ItemArea.Craft2,0),false,false);
                                guest.inventoryGesture.Release(guest.world.Items);
                                stage=3;stageFrame=frames;
                            }break;
                        case 3:
                            if(guest.multiplayerClient!.PendingCommands==0&&guest.recipeBook.Find(guest.world.Items!,2) is not null){
                                int result=guest.recipeBook.Craft(guest.world.Items!,2);
                                guest.SubmitClient(new(NetCommandKind.Craft){Size=2});if(result!=4)throw new InvalidOperationException("Predicted UI craft failed.");
                                stage=4;stageFrame=frames;
                            }break;
                        case 4:
                            if(guest.multiplayerClient!.PendingCommands==0&&guest.world.Items!.Cursor?.Id==new ContentId("blockcircuit:oak_planks")){
                                crafted=true;
                                multiplayerOpen=true;networkStatus=$"Loopback host / TCP {multiplayerHost!.Port} / 3 players";
                                stage=5;stageFrame=frames;
                            }break;
                        case 5:
                            if(frames-stageFrame>=8){Raylib.TakeScreenshot("multiplayer-host-preview.png");multiplayerOpen=false;stage=6;stageFrame=frames;}
                            break;
                        case 6:
                            if(frames-stageFrame>=8){Raylib.TakeScreenshot("multiplayer-world-preview.png");guest.inventoryOpen=true;stage=7;stageFrame=frames;}
                            break;
                        case 7:
                            if(frames-stageFrame>=8){Raylib.TakeScreenshot("multiplayer-inventory-preview.png");guest.inventoryOpen=false;stage=8;stageFrame=frames;}
                            break;
                    }
                }
                if(stage==7)guest.Render(guest.CurrentEye());else Render(CurrentEye());
                frames++;
                if(stage==8)break;
            }
            if(!placed||!replicated||!crafted||menuTicks==0||stage!=8||multiplayerHost!.ClientCount!=2)throw new InvalidOperationException($"Multiplayer smoke incomplete: stage {stage}, frame {frames}.");
            guest.LeaveMultiplayer();
            restored=ReferenceEquals(guest.world,originalSolo)&&originalBlocks.SequenceEqual(guest.world.Blocks)&&originalInventory.SequenceEqual(guest.world.Inventory);
            if(!restored)throw new InvalidOperationException("Leaving the session changed the guest's original solo world.");
        }catch(Exception e){failure=e.ToString();}
        finally{
            guest.LeaveMultiplayer();LeaveMultiplayer();guest.renderer.Dispose();renderer.Dispose();Raylib.CloseWindow();
        }
        bool resources=renderer.GpuBufferCount==0&&guest.renderer.GpuBufferCount==0&&renderer.GpuAllocationCount==renderer.ReleasedMeshCount&&guest.renderer.GpuAllocationCount==guest.renderer.ReleasedMeshCount;
        File.WriteAllText("multiplayer-smoke-results.json",JsonSerializer.Serialize(new{
            Passed=failure is null&&resources,Frames=frames,ElapsedSeconds=watch.Elapsed.TotalSeconds,RealTcpClients=2,SharedBlockReplicated=replicated,
            ClientGestureCraftConfirmed=crafted,HostTicksWhileMenuOpen=menuTicks,SoloWorldRestored=restored,AllGpuResourcesReleased=resources,
            HostGpuAllocations=renderer.GpuAllocationCount,HostGpuReleases=renderer.ReleasedMeshCount,
            ClientGpuAllocations=guest.renderer.GpuAllocationCount,ClientGpuReleases=guest.renderer.ReleasedMeshCount,Error=failure
        },new JsonSerializerOptions{WriteIndented=true}));
        if(failure is not null||!resources)throw new InvalidOperationException(failure??"Multiplayer GPU resources leaked.");
        Console.WriteLine("PASS: real TCP host + 2 clients: avatars, shared edit, UI gesture/craft, solo restoration and GPU teardown.");
    }
}

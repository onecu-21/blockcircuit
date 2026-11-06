using System.Net;
using System.Numerics;
using System.Text.Json;
using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    MultiplayerHost? multiplayerHost;
    MultiplayerClient? multiplayerClient;
    Task<NetPeer>? connectTask;
    CancellationTokenSource? connectCancellation;
    World? soloWorld;
    Vector3 soloFeet;
    float soloYaw,soloPitch;
    bool soloFlying;
    int soloSlot;
    bool multiplayerOpen,profileLoaded,joinedFromTitle;
    string networkAddress="127.0.0.1",networkPort="25565",networkName="Player",networkPassword="",networkStatus="",hostAddresses="";
    int networkField;
    Guid networkIdentity;
    long lastNetworkReply;
    readonly Dictionary<Guid,(Vector3 From,Vector3 To,double Time)> peerMotion=[];
    bool NetworkClientActive=>multiplayerClient is not null&&world.IsNetworkReplica&&soloWorld is not null;
    bool NetworkActive=>multiplayerHost is not null||NetworkClientActive;
    bool NetworkConnecting=>connectTask is not null||multiplayerClient is not null&&!multiplayerClient.Ready&&!world.IsNetworkReplica;
    record MultiplayerProfile(Guid Identity,string Name,string Address,int Port);
    NetPose LocalNetworkPose()=>new(feet,yaw,pitch,player.Flying);
    void EnsureNetworkProfile()
    {
        if(profileLoaded)return;
        profileLoaded=true;networkIdentity=Guid.NewGuid();
        try{
            string path=Path.Combine(dataDirectory,"multiplayer-profile.json");
            if(!File.Exists(path))return;
            var profile=JsonSerializer.Deserialize<MultiplayerProfile>(File.ReadAllText(path));
            if(profile is null||profile.Identity==Guid.Empty)return;
            networkIdentity=profile.Identity;
            if(ValidPlayerName(profile.Name))networkName=profile.Name;
            if(profile.Address?.Length is >0 and <=253)networkAddress=profile.Address;
            if(profile.Port is >0 and <=65535)networkPort=profile.Port.ToString();
        }catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException){networkStatus="Using a new local multiplayer profile.";}
    }
    static bool ValidPlayerName(string name)=>!string.IsNullOrWhiteSpace(name)&&name.Length<=24&&name.All(c=>c is >=' ' and <='~');
    void SaveNetworkProfile()
    {
        if(!ValidPlayerName(networkName)||!int.TryParse(networkPort,out int port)||port is <1 or >65535)throw new InvalidDataException("Enter a name and port from 1 to 65535.");
        AtomicSave.Write(Path.Combine(dataDirectory,"multiplayer-profile.json"),JsonSerializer.Serialize(new MultiplayerProfile(networkIdentity,networkName,networkAddress,port)));
    }
    void OpenMultiplayer()
    {
        EnsureNetworkProfile();multiplayerOpen=true;
        inventoryOpen=settingsOpen=newWorldOpen=false;searchFocused=false;inventoryGesture.Cancel();
        ResetSimulation();Raylib.EnableCursor();
    }
    void CloseMultiplayer()
    {
        if(NetworkConnecting){LeaveMultiplayer();networkStatus="Connection canceled.";}
        multiplayerOpen=false;ResetSimulation();
        if(FrontendVisible||paused)Raylib.EnableCursor();else Raylib.DisableCursor();
    }
    void BeginHosting()
    {
        if(!HasPlayableWorld){networkStatus="Open a singleplayer world before hosting it.";return;}
        try{
            if(NetworkActive||NetworkConnecting)return;
            EnsureNetworkProfile();SaveNetworkProfile();
            if(!int.TryParse(networkPort,out int port))return;
            world.EnableSlotInventory(palette.Hotbar);
            multiplayerHost=new(world,port,networkPassword,networkName,IPAddress.Any);
            hostAddresses=string.Join(", ",System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n=>n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n=>n.GetIPProperties().UnicastAddresses)
                .Where(a=>a.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork&&!IPAddress.IsLoopback(a.Address))
                .Select(a=>a.Address.ToString()).Distinct().Take(2));
            networkStatus=$"Hosting on TCP {multiplayerHost.Port}. Share your computer's IP.";
            paused=false;Notice("World opened for multiplayer. F8 manages the session.");
        }catch(Exception e)when(e is IOException or System.Net.Sockets.SocketException or System.Net.NetworkInformation.NetworkInformationException or ArgumentException or InvalidOperationException or UnauthorizedAccessException){networkStatus="Could not host: "+e.Message;}
    }
    void BeginJoining()
    {
        try{
            if(NetworkActive||NetworkConnecting)return;
            EnsureNetworkProfile();SaveNetworkProfile();
            if(string.IsNullOrWhiteSpace(networkAddress)||networkAddress.Length>253)throw new ArgumentException("Enter the host IP address or hostname.");
            joinedFromTitle=FrontendVisible;
            if(HasPlayableWorld){
                StopStreaming(true);
                world.Save(savePath,feet,yaw,pitch);
            }
            soloWorld=world;soloFeet=feet;soloYaw=yaw;soloPitch=pitch;soloFlying=player.Flying;soloSlot=palette.SelectedSlot;
            connectCancellation=new CancellationTokenSource();
            connectTask=NetPeer.ConnectAsync(networkAddress,int.Parse(networkPort),connectCancellation.Token);
            networkStatus="Connecting...";ResetSimulation();
        }catch(Exception e)when(e is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException){networkStatus="Could not connect: "+e.Message;LeaveMultiplayer();}
    }
    void PumpMultiplayer()
    {
        if(connectTask?.IsCompleted==true){
            var completed=connectTask;connectTask=null;
            try{
                var peer=completed.GetAwaiter().GetResult();
                multiplayerClient=new(peer,networkName,networkPassword,networkIdentity,world.Registries);
                networkStatus="Checking server and loading terrain...";
            }catch(Exception e){networkStatus="Connection failed: "+e.GetBaseException().Message;LeaveMultiplayer();}
            finally{connectCancellation?.Dispose();connectCancellation=null;}
        }
        if(multiplayerHost is not null){
            multiplayerHost.Pump(LocalNetworkPose());
            networkStatus=multiplayerHost.Status;
        }
        if(multiplayerClient is not null){
            var client=multiplayerClient;
            client.Pump(LocalNetworkPose());
            if(client.Error is {} error){
                bool showTitleError=joinedFromTitle;networkStatus="Disconnected: "+error;LeaveMultiplayer();
                if(showTitleError)multiplayerOpen=true;
                Notice(networkStatus);return;
            }
            if(client.Ready&&ReferenceEquals(world,soloWorld)&&client.World is {} replica){
                world=replica;palette.Blocks=world.Registries.Blocks;startup.EnterWorld();
                if(client.LocalPose is {} spawn){feet=spawn.Position;yaw=spawn.Yaw;pitch=spawn.Pitch;player.Flying=spawn.Flying;}
                player.VerticalVelocity=0;paused=false;ResetSimulation();SyncHotbar();
                networkStatus="Connected. F8 to leave.";
                multiplayerOpen=false;Raylib.DisableCursor();
            }
            else if(client.ConsumePositionCorrection() is {} corrected){
                feet=corrected.Position;previousFeet=feet;player.VerticalVelocity=0;
            }
            if(client.LastReply is {} reply&&reply.Sequence!=lastNetworkReply){
                lastNetworkReply=reply.Sequence;
                if(!reply.Accepted)Notice(reply.Reason);
            }
            if(!client.Ready)networkStatus=client.Status;
        }
        UpdatePeerMotion();
    }
    void LeaveMultiplayer()
    {
        if(connectTask is {} pending){
            connectCancellation?.Cancel();
            _=pending.ContinueWith(t=>{if(t.Status==TaskStatus.RanToCompletion)t.Result.Dispose();else _=t.Exception;},TaskScheduler.Default);
            connectTask=null;
        }
        connectCancellation?.Dispose();connectCancellation=null;
        multiplayerClient?.Dispose();multiplayerClient=null;
        multiplayerHost?.Dispose();multiplayerHost=null;
        if(soloWorld is not null){
            world=soloWorld;soloWorld=null;palette.Blocks=world.Registries.Blocks;
            feet=soloFeet;yaw=soloYaw;pitch=soloPitch;player.Flying=soloFlying;palette.SelectedSlot=soloSlot;
            player.VerticalVelocity=0;ResetSimulation();SyncHotbar();
        }
        if(joinedFromTitle){
            startup.ReturnToTitle();activeEntry=null;activeSavePath=null;
            if(renderer is not null)renderer.ResetWorldCache();
            if(Raylib.IsWindowReady())Raylib.EnableCursor();
            joinedFromTitle=false;
        }
        peerMotion.Clear();inventoryGesture.Cancel();lastNetworkReply=0;
    }
    bool SubmitClient(NetCommand command)
    {
        if(!NetworkClientActive)return false;
        if(multiplayerClient!.Request(command)==0)Notice("Connection is not ready for an action.");
        return true;
    }
    void SendInventoryGesture(string kind,ItemSlot[] slots,bool right)
    {
        if(!NetworkClientActive)return;
        SubmitClient(kind switch{
            "click"=>new(NetCommandKind.Click){Target=slots[0],Right=right},
            "shift"=>new(NetCommandKind.Shift){Source=slots[0]},
            _=>new(NetCommandKind.Drag){Targets=slots,Right=right}
        });
    }
    void IdleNetworkTick(float dt)
    {
        actions.Clear();simulationInput.Reset();controls.Reset();mining=null;mineTime=0;mineHeld=false;
        if(!NetworkClientActive)world.Tick();
        previousFeet=feet;
        if(NetworkClientActive&&!multiplayerClient!.Ready){player.VerticalVelocity=0;SyncHotbar();return;}
        player.Step(world,Vector3.Zero,PhysicsProfile.WalkSpeed,false,0,dt,false);
        if(!NetworkClientActive)world.CollectDrops(feet);
        SyncHotbar();
    }
    IEnumerable<RemotePlayerState> OtherPlayers()
    {
        var list=multiplayerHost?.Players??multiplayerClient?.Players??Array.Empty<RemotePlayerState>();
        Guid local=multiplayerHost?.LocalPlayerId??multiplayerClient?.LocalPlayerId??Guid.Empty;
        return list.Where(p=>p.Id!=local);
    }
    Vector3 PeerPosition(RemotePlayerState peer)
    {
        if(!peerMotion.TryGetValue(peer.Id,out var motion))return peer.Position;
        return Vector3.Lerp(motion.From,motion.To,(float)Math.Clamp((Raylib.GetTime()-motion.Time)/.1,0,1));
    }
    void UpdatePeerMotion()
    {
        var peers=OtherPlayers().ToArray();var ids=peers.Select(p=>p.Id).ToHashSet();
        foreach(var id in peerMotion.Keys.Where(id=>!ids.Contains(id)).ToArray())peerMotion.Remove(id);
        foreach(var peer in peers){
            if(!peerMotion.TryGetValue(peer.Id,out var old))peerMotion[peer.Id]=(peer.Position,peer.Position,Raylib.GetTime());
            else if(old.To!=peer.Position)peerMotion[peer.Id]=(PeerPosition(peer),peer.Position,Raylib.GetTime());
        }
    }
    void DrawRemotePlayers()
    {
        foreach(var peer in OtherPlayers()){
            var position=PeerPosition(peer);
            if(Vector3.DistanceSquared(position,feet)>settings.DrawDistance*settings.DrawDistance*4)continue;
            uint hash=(uint)peer.Id.GetHashCode();var shirt=new Color((byte)(65+hash%110),(byte)(90+(hash>>8)%100),(byte)(110+(hash>>16)%110),(byte)255);
            Rlgl.PushMatrix();
            try{
                Rlgl.Translatef(position.X,position.Y,position.Z);Rlgl.Rotatef(peer.Yaw*180/MathF.PI,0,1,0);
                Raylib.DrawCube(new(-.12f,.32f,0),.2f,.64f,.25f,ink);
                Raylib.DrawCube(new(.12f,.32f,0),.2f,.64f,.25f,ink);
                Raylib.DrawCube(new(0,1.07f,0),.43f,.84f,.26f,shirt);
                Raylib.DrawCube(new(-.29f,1.07f,0),.14f,.8f,.22f,shirt);Raylib.DrawCube(new(.29f,1.07f,0),.14f,.8f,.22f,shirt);
                Rlgl.Translatef(0,1.62f,0);Rlgl.Rotatef(-peer.Pitch*180/MathF.PI,1,0,0);
                Raylib.DrawCube(Vector3.Zero,.38f,.38f,.38f,new Color(211,169,129,255));
                Raylib.DrawCube(new(-.09f,.04f,.193f),.045f,.04f,.008f,ink);Raylib.DrawCube(new(.09f,.04f,.193f),.045f,.04f,.008f,ink);
            }finally{Rlgl.PopMatrix();}
        }
    }
    void DrawPeerNames(Camera3D camera)
    {
        if(hudHidden)return;
        foreach(var peer in OtherPlayers()){
            var position=PeerPosition(peer)+new Vector3(0,2,0);
            if(Vector3.Dot(position-camera.Position,Vector3.Normalize(camera.Target-camera.Position))<=0||Vector3.DistanceSquared(position,feet)>settings.DrawDistance*settings.DrawDistance)continue;
            var screen=Raylib.GetWorldToScreen(position,camera);int size=14,width=Raylib.MeasureText(peer.Name,size);
            Raylib.DrawRectangle((int)screen.X-width/2-5,(int)screen.Y-4,width+10,22,ink);
            Raylib.DrawText(peer.Name,(int)screen.X-width/2,(int)screen.Y,size,accent);
        }
    }
    void DrawMultiplayerPanel()
    {
        int w=Math.Min(760,Raylib.GetScreenWidth()-48),h=510,x=(Raylib.GetScreenWidth()-w)/2,y=(Raylib.GetScreenHeight()-h)/2;
        Raylib.DrawRectangle(0,0,Raylib.GetScreenWidth(),Raylib.GetScreenHeight(),new Color(9,16,24,220));Raylib.DrawRectangle(x,y,w,h,ink);Raylib.DrawRectangle(x,y,4,h,accent);
        Raylib.DrawText("MULTIPLAYER",x+26,y+22,28,Color.White);
        DrawTextFit(HasPlayableWorld?"Open this world, or join a host by IP address. Shared worlds keep running in menus.":"Join a host by IP address. To host a world, open it from Singleplayer first.",x+26,y+63,14,w-52,muted);
        string[] labels=["PLAYER NAME","HOST IP / HOSTNAME","TCP PORT","PASSWORD (optional)"];
        string[] values=[networkName,networkAddress,networkPort,networkPassword];
        for(int i=0;i<4;i++){
            int row=y+105+i*59;var box=new Rectangle(x+230,row,w-256,38);
            Raylib.DrawText(labels[i],x+26,row+12,14,Color.White);Raylib.DrawRectangleRec(box,tileColor);
            if(Raylib.IsMouseButtonPressed(MouseButton.Left)&&Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(),box))networkField=i;
            if(i==networkField)Raylib.DrawRectangleLinesEx(box,1,accent);
            string shown=i==3?new string('*',values[i].Length):values[i];
            DrawTextFit(shown+(i==networkField?"_":""),x+240,row+12,16,w-280,Color.White);
        }
        if(!NetworkActive&&!NetworkConnecting){
            int editingField=networkField;int c;string text=values[editingField];int max=editingField switch{0=>24,1=>253,2=>5,_=>64};
            while((c=Raylib.GetCharPressed())!=0)if(c is >=32 and <=126&&text.Length<max&&(networkField!=2||char.IsAsciiDigit((char)c)))text+=(char)c;
            if(Raylib.IsKeyPressed(KeyboardKey.Backspace)&&text.Length>0)text=text[..^1];
            if(Raylib.IsKeyPressed(KeyboardKey.Tab))networkField=(networkField+1)%4;
            switch(editingField){case 0:networkName=text;break;case 1:networkAddress=text;break;case 2:networkPort=text;break;case 3:networkPassword=text;break;}
        }
        DrawTextFit(networkStatus.Length==0?"Host uses the current world. Joining preserves your local singleplayer save.":networkStatus,x+26,y+354,14,w-52,accent);
        if(NetworkActive||NetworkConnecting){
            DrawTextFit($"Players: {1+(multiplayerHost?.ClientCount??OtherPlayers().Count())} / 9"+(multiplayerHost is null||hostAddresses.Length==0?"":"  |  Host IP: "+hostAddresses),x+26,y+385,14,w-52,muted);
            if(Button(new(x+26,y+h-67,240,40),NetworkActive?"LEAVE / CLOSE SESSION":"CANCEL CONNECTION")){LeaveMultiplayer();networkStatus=FrontendVisible?"Disconnected. Connect again or choose a world.":"Returned to singleplayer.";Notice(networkStatus);}
        }else{
            if(HasPlayableWorld&&Button(new(x+26,y+h-67,180,40),"HOST WORLD",true))BeginHosting();
            if(Button(new(x+(HasPlayableWorld?220:26),y+h-67,HasPlayableWorld?180:374,40),"JOIN BY IP",!HasPlayableWorld))BeginJoining();
        }
        if(Button(new(x+w-142,y+h-67,116,40),"BACK"))CloseMultiplayer();
    }
}

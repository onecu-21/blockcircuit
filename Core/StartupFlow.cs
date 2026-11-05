namespace BlockCircuit;

public enum StartupScreen { Title, Worlds, CreateWorld, Loading, RenameWorld, DeleteWorld, Playing }

/// <summary>Front-end navigation. World I/O and save success are supplied by the session owner.</summary>
public sealed class StartupFlow
{
    StartupScreen loadingOrigin;
    public StartupScreen Screen { get; private set; }
    public bool HasWorld=>Screen==StartupScreen.Playing;
    public bool WaitingForInputRelease { get; private set; }
    public string Status { get; private set; }="";
    public StartupFlow(bool playing=false){Screen=playing?StartupScreen.Playing:StartupScreen.Title;}
    void Require(params StartupScreen[] allowed)
    {if(!allowed.Contains(Screen))throw new InvalidOperationException("Finish or cancel the current screen first.");}
    public void ShowWorlds(bool empty=false)
    {
        Require(StartupScreen.Title,StartupScreen.Worlds);
        Screen=empty?StartupScreen.CreateWorld:StartupScreen.Worlds;Status="";
    }
    public void ShowCreateWorld(){Require(StartupScreen.Worlds);Screen=StartupScreen.CreateWorld;Status="";}
    public void ShowRename(){Require(StartupScreen.Worlds);Screen=StartupScreen.RenameWorld;Status="";}
    public void ShowDelete(){Require(StartupScreen.Worlds);Screen=StartupScreen.DeleteWorld;Status="";}
    public void BeginLoading()
    {
        Require(StartupScreen.Worlds,StartupScreen.CreateWorld);
        loadingOrigin=Screen;Screen=StartupScreen.Loading;Status="";
    }
    public void EnterWorld(){Require(StartupScreen.Loading,StartupScreen.Title,StartupScreen.Playing);Screen=StartupScreen.Playing;WaitingForInputRelease=true;Status="";}
    public void FailLoading(string message){Require(StartupScreen.Loading);Screen=loadingOrigin;Status=message;}
    public bool AcceptGameplayInput(bool leftHeld,bool rightHeld,bool confirmHeld)
    {
        if(!HasWorld)return false;
        if(!WaitingForInputRelease)return true;
        if(!leftHeld&&!rightHeld&&!confirmHeld)WaitingForInputRelease=false;
        return false;
    }
    public void Back()
    {
        switch(Screen){
            case StartupScreen.Worlds:Screen=StartupScreen.Title;break;
            case StartupScreen.CreateWorld:case StartupScreen.RenameWorld:case StartupScreen.DeleteWorld:Screen=StartupScreen.Worlds;break;
        }
        if(Screen!=StartupScreen.Loading)Status="";
    }
    public void ReturnToTitle(){Require(StartupScreen.Playing,StartupScreen.Title,StartupScreen.Worlds);Screen=StartupScreen.Title;Status="";}
}

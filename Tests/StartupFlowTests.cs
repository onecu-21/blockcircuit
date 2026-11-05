namespace BlockCircuit;

public static class StartupFlowTests
{
    public static void Run()
    {
        int checks=0;
        void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);Console.WriteLine("PASS: "+message);checks++;}
        static bool Rejected(Action action){try{action();return false;}catch(InvalidOperationException){return true;}}
        var flow=new StartupFlow();
        Check(flow.Screen==StartupScreen.Title&&!flow.HasWorld,"first launch starts on the title without an active world");
        Check(Rejected(flow.BeginLoading)&&flow.Screen==StartupScreen.Title&&!flow.HasWorld,
            "a load cannot bypass world selection from the title screen");

        flow.ShowWorlds(true);
        Check(flow.Screen==StartupScreen.CreateWorld&&!flow.HasWorld,"singleplayer with no worlds opens the explicit create-world flow");
        flow.Back();
        Check(flow.Screen==StartupScreen.Worlds&&!flow.HasWorld,"canceling first creation returns to an empty selectable world list");
        flow.Back();
        Check(flow.Screen==StartupScreen.Title&&!flow.HasWorld,"back from the world list returns to the title without starting play");

        flow.ShowWorlds();
        flow.ShowCreateWorld();
        flow.BeginLoading();
        flow.Back();
        Check(flow.Screen==StartupScreen.Loading&&!flow.HasWorld,"back during a committed load cannot expose play or discard an in-progress operation");
        Check(Rejected(flow.ShowDelete)&&flow.Screen==StartupScreen.Loading,"a world cannot be deleted while its create or load operation is running");
        flow.FailLoading("Could not save this world.");
        Check(flow.Screen==StartupScreen.CreateWorld&&flow.Status=="Could not save this world."&&!flow.HasWorld,
            "failed creation restores the form with the actual error and no active world");
        flow.BeginLoading();flow.EnterWorld();
        Check(flow.Screen==StartupScreen.Playing&&flow.HasWorld&&flow.Status.Length==0,"only a successful explicit create or open publishes a playable world");
        flow.Back();
        Check(flow.HasWorld&&flow.Screen==StartupScreen.Playing,"ordinary back navigation does not silently abandon an active world");
        flow.ReturnToTitle();
        Check(flow.Screen==StartupScreen.Title&&!flow.HasWorld,"the session owner can return to the title after a successful save-and-quit");

        flow.ShowWorlds();
        Check(Rejected(flow.EnterWorld)&&flow.Screen==StartupScreen.Worlds,"a listed world cannot become active before its loading step");
        flow.BeginLoading();flow.FailLoading("Unreadable save.");
        Check(flow.Screen==StartupScreen.Worlds&&flow.Status=="Unreadable save.","a failed existing-world open returns to the world list with its error");
        flow.ShowRename();flow.Back();
        Check(flow.Screen==StartupScreen.Worlds&&!flow.HasWorld,"canceling world details returns to selection without entering play");
        flow.ShowDelete();flow.Back();
        Check(flow.Screen==StartupScreen.Worlds&&!flow.HasWorld,"canceling delete confirmation returns to selection without starting a world");
        flow.Back();flow.EnterWorld();
        Check(flow.HasWorld,"a successfully connected multiplayer session can enter directly from the title");
        Check(!flow.AcceptGameplayInput(true,false,false)&&flow.WaitingForInputRelease,"the click that confirmed a world cannot immediately mine in the newly entered world");
        Check(!flow.AcceptGameplayInput(false,false,true)&&flow.WaitingForInputRelease,"holding Enter after world selection keeps gameplay input gated");
        Check(!flow.AcceptGameplayInput(false,false,false)&&!flow.WaitingForInputRelease&&flow.AcceptGameplayInput(true,false,false),
            "gameplay resumes only after an entire released-input frame");
        var editor=new MenuText{Text="Old World"};editor.SelectAll();editor.Insert("New World",64);
        Check(editor.Text=="New World"&&editor.Cursor==9&&!editor.SelectedAll,"Ctrl+A followed by typing or paste replaces the whole world name");
        editor.Home();for(int i=0;i<3;i++)editor.Move(1);editor.Insert("er",64);editor.Backspace();editor.Delete();
        Check(editor.Text=="NeweWorld","cursor insertion, backspace and delete edit a world name at the intended position");
        editor.Text="";editor.Insert("\r\nSafe\tName",64);
        Check(editor.Text=="SafeName","pasted line breaks and control characters cannot become a world name");
        editor.Text="";editor.Insert(new string('a',80),64);
        Check(editor.Text.Length==64&&editor.Cursor==64,"name paste respects the same 64-character limit as storage validation");
        editor.Text="A😀B";editor.End();editor.Move(-1);editor.Move(-1);editor.Delete();
        Check(editor.Text=="AB","cursor movement and delete preserve complete Unicode scalar boundaries");
        editor.Text="A😀B";editor.End();editor.Backspace();editor.Backspace();
        Check(editor.Text=="A","backspace removes an entire supplementary Unicode character rather than half a surrogate pair");
        editor.Text="";editor.Insert("😀",1);
        Check(editor.Text.Length==0,"maximum-length truncation never publishes half of a supplementary character");
        var smoke=new StartupFlow(playing:true);
        Check(smoke.HasWorld&&smoke.Screen==StartupScreen.Playing,"existing gameplay smoke scenarios can explicitly bypass the title");
        Check(smoke.AcceptGameplayInput(true,true,true),"explicit gameplay smoke initialization does not inherit a nonexistent menu click gate");
        Console.WriteLine($"Startup flow tests passed: {checks}");
    }
}

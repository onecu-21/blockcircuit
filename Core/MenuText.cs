namespace BlockCircuit;

/// <summary>Text editing shared by the title world's name, search, rename and seed fields.</summary>
public sealed class MenuText
{
    string text="";
    public string Text { get=>text; set { text=value; Cursor=text.Length; SelectedAll=false; } }
    public int Cursor { get; private set; }
    public bool SelectedAll { get; private set; }
    public void SelectAll(){Cursor=text.Length;SelectedAll=true;}
    public void Move(int direction){
        if(SelectedAll){Cursor=direction<0?0:text.Length;SelectedAll=false;return;}
        if(direction<0&&Cursor>0)Cursor-=Cursor>=2&&char.IsSurrogatePair(text,Cursor-2)?2:1;
        else if(direction>0&&Cursor<text.Length)Cursor+=char.IsSurrogatePair(text,Cursor)?2:1;
    }
    public void Home(){Cursor=0;SelectedAll=false;}
    public void End(){Cursor=text.Length;SelectedAll=false;}
    public void Insert(string value,int maximum)
    {
        value=new string(value.Where(c=>!char.IsControl(c)).ToArray());
        int room=maximum-(SelectedAll?0:text.Length);
        if(room<=0||value.Length==0)return;
        if(value.Length>room){if(room>0&&char.IsHighSurrogate(value[room-1]))room--;value=value[..room];}
        if(value.Length==0)return;
        if(SelectedAll){text="";Cursor=0;SelectedAll=false;}
        text=text.Insert(Cursor,value);Cursor+=value.Length;
    }
    public void Backspace()
    {
        if(SelectedAll){text="";Cursor=0;SelectedAll=false;}
        else if(Cursor>0){int count=Cursor>=2&&char.IsSurrogatePair(text,Cursor-2)?2:1;text=text.Remove(Cursor-count,count);Cursor-=count;}
    }
    public void Delete()
    {
        if(SelectedAll){text="";Cursor=0;SelectedAll=false;}
        else if(Cursor<text.Length)text=text.Remove(Cursor,char.IsSurrogatePair(text,Cursor)?2:1);
    }
}

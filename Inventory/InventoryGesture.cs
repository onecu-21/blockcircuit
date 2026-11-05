namespace BlockCircuit;

/// <summary>Frame-independent mouse gesture state; UI hit testing supplies slot identities.</summary>
public sealed class InventoryGesture
{
    PlayerInventory? owner;
    ItemSlot? origin;
    bool right,picked;
    readonly List<ItemSlot> targets=[];
    public Action<string,ItemSlot[],bool>? Performed { get; set; }
    public bool Active=>origin is not null;
    public bool RightButton=>right;
    public void Cancel(){origin=null;owner=null;targets.Clear();}
    public void Begin(PlayerInventory bag,ItemSlot target,bool rightButton,bool shift)
    {
        Cancel();
        if(shift){bag.Shift(target);Performed?.Invoke("shift",[target],false);return;}
        owner=bag;origin=target;right=rightButton;picked=bag.Cursor is null;
        if(picked){bag.Click(target,right);Performed?.Invoke("click",[target],right);}else targets.Add(target);
    }
    public void Hover(PlayerInventory bag,ItemSlot target)
    {
        if(!ReferenceEquals(owner,bag)){Cancel();return;}
        if(origin is not null&&(!picked||target!=origin)&&!targets.Contains(target))targets.Add(target);
    }
    public void Release(PlayerInventory bag)
    {
        if(!ReferenceEquals(owner,bag)){Cancel();return;}
        if(targets.Count==1&&!right){bag.Click(targets[0]);Performed?.Invoke("click",targets.ToArray(),false);}
        else if(targets.Count>0){bag.Drag(targets,right);Performed?.Invoke("drag",targets.ToArray(),right);}
        Cancel();
    }
}

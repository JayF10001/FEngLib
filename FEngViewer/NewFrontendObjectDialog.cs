using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using FEngLib.Objects;
using FEngLib.Packages;
using FEngLib.Utils;

namespace FEngViewer;

internal sealed class NewFrontendObjectDialog:Form
{
    private record Choice(string Caption, object Value) { public override string ToString()=>Caption; }
    private readonly ComboBox kind=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    private readonly ComboBox parent=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    private readonly ComboBox resource=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    private readonly TextBox name=new(){Dock=DockStyle.Fill,CharacterCasing=CharacterCasing.Upper,MaxLength=48};
    public ObjectType Kind=>(ObjectType)((Choice)kind.SelectedItem).Value;
    public string ObjectName=>name.Text.Trim();
    public IObject<ObjectData> ParentObject=>((Choice)parent.SelectedItem).Value as IObject<ObjectData>;
    public ResourceRequest Resource=>((Choice)resource.SelectedItem).Value as ResourceRequest;
    public NewFrontendObjectDialog(Package package,IObject<ObjectData> selected)
    {
        Text="新建对象"; StartPosition=FormStartPosition.CenterParent; ClientSize=new Size(540,Math.Max(400,Font.Height*16+100));
        MinimumSize=new Size(520,440); MaximizeBox=false; MinimizeBox=false;
        var table=new TableLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,Padding=new Padding(16),ColumnCount=2,RowCount=6};
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,110)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        void Row(string title,Control value,int row)
        { table.RowStyles.Add(new RowStyle(SizeType.AutoSize)); table.Controls.Add(new Label{Text=title,AutoSize=true,Margin=new Padding(0,7,8,8)},0,row); value.Margin=new Padding(0,4,0,8); table.Controls.Add(value,1,row); }
        foreach(var pair in new[]{("图像",ObjectType.Image),("文本",ObjectType.String),("组",ObjectType.Group),
            ("简单图像",ObjectType.SimpleImage),("彩色图像",ObjectType.ColoredImage),("多图图像",ObjectType.MultiImage)}) kind.Items.Add(new Choice(pair.Item1,pair.Item2));
        parent.Items.Add(new Choice("根级",null));
        foreach(var obj in package.Objects.OfType<Group>()) parent.Items.Add(new Choice(obj.Name??$"{obj.NameHash:X8}",obj));
        var wanted=selected is Group?selected:selected?.Parent;
        parent.SelectedIndex=Enumerable.Range(0,parent.Items.Count).FirstOrDefault(i=>ReferenceEquals(((Choice)parent.Items[i]).Value,wanted));
        var hash=new Label{AutoSize=true,ForeColor=EditorTheme.Muted};
        var error=new Label{AutoSize=true,MaximumSize=new Size(400,0)};
        var ok=new Button{Text="创建",DialogResult=DialogResult.None,AutoSize=true};
        void ValidateName()
        {
            var n=ObjectName; var h=Hashing.BinHash(n); hash.Text=$"0x{h:X8}";
            var valid=n.Length>0 && n.All(c=>c>32 && c<127 && c!='"');
            var duplicate=package.Objects.Any(o=>o.NameHash==h || string.Equals(o.Name,n,StringComparison.OrdinalIgnoreCase));
            error.Text=!valid?"请输入 ASCII 名称，不含空格和双引号。":duplicate?"此名称或哈希已经存在。":"使用包内已有资源；不会向游戏添加贴图。";
            ok.Enabled=valid&&!duplicate;
        }
        kind.SelectedIndexChanged+=(_,_)=>
        {
            resource.Items.Clear(); resource.Items.Add(new Choice("（未绑定资源）",null));
            foreach(var r in package.ResourceRequests.Where(r=>Kind==ObjectType.String?r.Type==ResourceType.Font:
                Kind is ObjectType.Image or ObjectType.ColoredImage or ObjectType.MultiImage && (r.Type==ResourceType.Image || Kind==ObjectType.MultiImage && r.Type==ResourceType.MultiImage)))
                resource.Items.Add(new Choice(r.Name??$"0x{r.ID:X8}",r));
            resource.SelectedIndex=resource.Items.Count>1?1:0;
        };
        name.TextChanged+=(_,_)=>ValidateName();
        Row("对象类型",kind,0); Row("名称",name,1); Row("名称哈希",hash,2); Row("父级",parent,3); Row("资源",resource,4);
        var bottom=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,Padding=new Padding(16),FlowDirection=FlowDirection.RightToLeft};
        var cancel=new Button{Text="取消",DialogResult=DialogResult.Cancel,AutoSize=true};
        ok.Click+=(_,_)=>{ValidateName();if(ok.Enabled) DialogResult=DialogResult.OK;};
        bottom.Controls.Add(cancel);bottom.Controls.Add(ok);
        table.Controls.Add(error,0,5);table.SetColumnSpan(error,2);
        Controls.Add(table);Controls.Add(bottom);AcceptButton=ok;CancelButton=cancel;
        kind.SelectedIndex=0; name.Text="NEW_OBJECT";ValidateName();EditorTheme.Apply(this);
    }
}

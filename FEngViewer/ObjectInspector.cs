using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using FEngLib.Objects;
using FEngLib.Packages;
using FEngLib.Structures;
using FEngRender.Data;
using FEngViewer.Wrappers;

namespace FEngViewer;

/// <summary>Common controls edit the same model and history as the advanced property grid.</summary>
internal sealed class ObjectInspector:UserControl
{
    private readonly Label caption=new(){Dock=DockStyle.Top,AutoSize=false,Height=66,Padding=new Padding(10,8,6,4)};
    private readonly TabControl tabs=new(){Dock=DockStyle.Fill};
    private readonly TabPage common=new("常用");
    private readonly TabPage advanced=new("高级");
    private readonly TableLayoutPanel table=new(){Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Padding=new Padding(8)};
    private readonly PropertyGrid grid;
    private bool building;
    private IObject<ObjectData> current;
    private RenderTreeNode node;
    private Package package;
    private string textureDirectory;
    private readonly Font sectionFont;
    private bool wide;
    public event Action<string,Action> EditRequested;
    public event Action<IObject<ObjectData>,uint> PlayRequested;
    public event Action PauseRequested;
    public event Action ResetRequested;
    public ObjectInspector(PropertyGrid advancedGrid)
    {
        Dock=DockStyle.Fill; grid=advancedGrid;
        sectionFont=new Font(Font,FontStyle.Bold);
        common.AutoScroll=true; advanced.AutoScroll=true;
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,38));table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,62));
        common.Controls.Add(table);advanced.Controls.Add(grid);grid.Dock=DockStyle.Fill;
        tabs.TabPages.Add(common);tabs.TabPages.Add(advanced);
        Controls.Add(tabs);Controls.Add(caption);
        common.Resize+=(_,_)=>
        {if(!building && !ContainsFocus && wide!=(common.ClientSize.Width>=460))Rebuild();};
    }
    public void ShowTarget(object target,RenderTreeNode selected,Package document,string textures,bool structure=false)
    {
        grid.SelectedObject=target;
        node=selected;current=selected?.GetObject();package=document;textureDirectory=textures;
        caption.Text=current==null?target==null?"未选中对象\n从对象树或画布选择对象":"结构属性\n可检查原始字段":
            $"{current.Name??"未命名对象"}\n{TypeName(current.Type)}   0x{current.NameHash:X8}   GUID {current.Guid:X8}";
        if(target!=null && (selected==null || structure)) tabs.SelectedTab=advanced;
        Rebuild();
    }
    public void RefreshGeometry()
    {
        if(ContainsFocus) return;
        if(node==null)return;
        var pose=node.EvaluatedData;
        void Update(string name,float value)
        {
            if(!float.IsFinite(value))return;
            if(table.Controls.Find("edit_"+name,true).FirstOrDefault() is NumericUpDown n)
            {var v=Math.Clamp((decimal)value,n.Minimum,n.Maximum);n.Tag=v;n.Value=v;}
        }
        for(var i=0;i<3;i++) {Update("位置 "+"XYZ"[i],Component(pose.Position,i));Update("大小 "+"XYZ"[i],Component(pose.Size,i));}
        Update("旋转 Z（度）",Angle(pose.Rotation));
        grid.Refresh();
    }
    private void Rebuild()
    {
        building=true;table.SuspendLayout();
        wide=common.ClientSize.Width>=460;
        foreach(Control c in table.Controls.Cast<Control>().ToArray()) { table.Controls.Remove(c);c.Dispose(); }
        table.RowCount=0;table.RowStyles.Clear();
        if(current==null) Row("",new Label{Text="选择对象后可编辑常用属性。",AutoSize=true});
        else
        {
            Header("布局变换 · 全部已有脚本");
            if(node.PoseError!=null) Row("轨道状态",new Label{Text=node.PoseError,AutoSize=true,MaximumSize=new Size(250,0)});
            var pose=node.EvaluatedData;
            var init=current.GetScripts().FirstOrDefault(s=>s.Id==0x1744B3);
            Row("数值来源",new Label{Text=node.PoseError!=null?"静态回退（轨道无法求值）":init==null?"静态属性":"INIT · 第 0 帧",AutoSize=true});
            Vector("位置",pose.Position,(axis,value)=>
            {
                var p=node.EvaluatedData.Position;var desired=With(p,axis,value);
                ObjectPropertyEditing.SetPosition(current,current.Data.Position+desired-p);
            });
            Vector("大小",pose.Size,(axis,value)=>
            {
                var old=Component(node.EvaluatedData.Size,axis);
                if(Math.Abs(old)<0.000001f) throw new InvalidOperationException("零尺寸轴无法按比例缩放。");
                ObjectPropertyEditing.Scale(current,With(Vector3.One,axis,value/old));
            });
            var q=pose.Rotation;
            var angle=Angle(q);
            Number("旋转 Z（度）",angle,v=>ObjectPropertyEditing.Rotate(current,
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ,((float)v-angle)*MathF.PI/180)));
            Vector("旋转中心",pose.Pivot,(axis,value)=>ObjectPropertyEditing.SetPivot(current,
                current.Data.Pivot+With(node.EvaluatedData.Pivot,axis,value)-node.EvaluatedData.Pivot));
            var visible=new CheckBox{Text="游戏可见",Checked=!current.Flags.HasFlag(ObjectFlags.Invisible),AutoSize=true};
            visible.CheckedChanged+=(_,_)=>Apply("切换可见性",()=>current.SetFlag(ObjectFlags.Invisible,!visible.Checked));Row("可见性",visible);
            var editorVisible=new CheckBox{Text="预览显示",Checked=!current.Flags.HasFlag(ObjectFlags.HideInEdit),AutoSize=true};
            editorVisible.CheckedChanged+=(_,_)=>Apply("切换预览显示",()=>current.SetFlag(ObjectFlags.HideInEdit,!editorVisible.Checked));Row("编辑器",editorVisible);
            Header("基础颜色与透明度");
            var color=current.Data.Color;
            foreach(var channel in new[]{"R","G","B","A"})
            {
                var ch=channel;
                Number(ch,ch switch{"R"=>color.Red,"G"=>color.Green,"B"=>color.Blue,_=>color.Alpha},v=>
                {
                    var c=current.Data.Color;
                    if(ch=="R") c.Red=(int)v; else if(ch=="G") c.Green=(int)v;else if(ch=="B") c.Blue=(int)v;else c.Alpha=(int)v;
                    ObjectPropertyEditing.SetColor(current,c);
                },0,255,0);
            }
            var picker=new Button{Text="拾取颜色…",AutoSize=true};
            picker.Click+=(_,_)=>
            {
                var c=current.Data.Color;
                using var dlg=new ColorDialog{Color=Color.FromArgb(Clamp(c.Red),Clamp(c.Green),Clamp(c.Blue))};
                if(dlg.ShowDialog(FindForm())==DialogResult.OK) Apply("修改颜色",()=>ObjectPropertyEditing.SetColor(current,
                    new Color4(dlg.Color.B,dlg.Color.G,dlg.Color.R,c.Alpha)));
            };Row("",picker);
            if(current is Text text)
            {
                Header("文本");
                TextField("内容",text.Value,v=>text.Value=v,true);
                TextField("本地化标签",text.Label,v=>
                {text.Label=v;text.Hash=FEngLib.Utils.Hashing.BinHash(v);});
                Number("标签哈希",text.Hash,v=>text.Hash=(uint)v,0,uint.MaxValue,0);
                Number("最大宽度",text.MaxWidth,v=>text.MaxWidth=(uint)v,0,uint.MaxValue,0);
                Number("行距",text.Leading,v=>text.Leading=(int)v,int.MinValue,int.MaxValue,0);
                Number("缓冲长度",text.BufferLength,v=>text.BufferLength=(uint)v,0,uint.MaxValue,0);
                // The format is a bit field; preserve unrelated bits when changing alignment.
                Options("水平对齐",new[]{"左对齐","居中","右对齐"},(int)((uint)text.Formatting&3),v=>
                    text.Formatting=(TextFormat)(((uint)text.Formatting&~3u)|(uint)v));
                Options("垂直对齐",new[]{"顶部","居中","底部"},(int)(((uint)text.Formatting>>2)&3),v=>
                    text.Formatting=(TextFormat)(((uint)text.Formatting&~12u)|((uint)v<<2)));
                ResourcePicker(ResourceType.Font);
            }
            else if(current.Type is ObjectType.Image or ObjectType.ColoredImage or ObjectType.MultiImage)
            {
                Header("图像资源与 UV"); ResourcePicker(ResourceType.Image);
                if(current.Data is ImageData image)
                {
                    Uv("左上 UV",image.UpperLeft,v=>image.UpperLeft=v);
                    Uv("右下 UV",image.LowerRight,v=>image.LowerRight=v);
                }
                if(current is ColoredImage ci)
                {
                    CornerColor("左上颜色",()=>ci.Data.TopLeft,v=>ci.Data.TopLeft=v);
                    CornerColor("右上颜色",()=>ci.Data.TopRight,v=>ci.Data.TopRight=v);
                    CornerColor("左下颜色",()=>ci.Data.BottomLeft,v=>ci.Data.BottomLeft=v);
                    CornerColor("右下颜色",()=>ci.Data.BottomRight,v=>ci.Data.BottomRight=v);
                }
                if(current is MultiImage mi)
                {
                    Header("多图层（资源 ID）");
                    Number("纹理 1",mi.Texture1,v=>mi.Texture1=(uint)v,0,uint.MaxValue,0);
                    Number("纹理 2",mi.Texture2,v=>mi.Texture2=(uint)v,0,uint.MaxValue,0);
                    Number("纹理 3",mi.Texture3,v=>mi.Texture3=(uint)v,0,uint.MaxValue,0);
                    Uv("图层 1 左上",mi.Data.TopLeft1,v=>mi.Data.TopLeft1=v);Uv("图层 1 右下",mi.Data.BottomRight1,v=>mi.Data.BottomRight1=v);
                    Uv("图层 2 左上",mi.Data.TopLeft2,v=>mi.Data.TopLeft2=v);Uv("图层 2 右下",mi.Data.BottomRight2,v=>mi.Data.BottomRight2=v);
                    Uv("图层 3 左上",mi.Data.TopLeft3,v=>mi.Data.TopLeft3=v);Uv("图层 3 右下",mi.Data.BottomRight3,v=>mi.Data.BottomRight3=v);
                }
            }
            Header("已有脚本预览");
            var scripts=current.GetScripts().ToArray();
            var select=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
            foreach(var s in scripts) select.Items.Add($"{s.Name??$"0x{s.Id:X8}"} · {s.Length} ms");
            if(scripts.Length>0) select.SelectedIndex=0;Row("脚本",select);
            var buttons=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill};
            foreach(var title in new[]{"播放","暂停","初始布局"})
            {
                var b=new Button{Text=title,AutoSize=true};
                b.Click+=(_,_)=>{if(title=="播放"&&select.SelectedIndex>=0)PlayRequested?.Invoke(current,scripts[select.SelectedIndex].Id);
                    else if(title=="暂停")PauseRequested?.Invoke();else if(title=="初始布局")ResetRequested?.Invoke();};buttons.Controls.Add(b);
            }
            Row("",buttons);
        }
        EditorTheme.Apply(this);table.ResumeLayout();building=false;
    }
    private static int Clamp(int n)=>Math.Clamp(n,0,255);
    private void CornerColor(string label,Func<Color4> get,Action<Color4> set)
    {
        var b=new Button{Text="选择…",AutoSize=true};
        b.Click+=(_,_)=>{var c=get();using var dlg=new ColorDialog{Color=Color.FromArgb(Clamp(c.Red),Clamp(c.Green),Clamp(c.Blue))};
            if(dlg.ShowDialog(FindForm())==DialogResult.OK)Apply(label,()=>set(new Color4(dlg.Color.B,dlg.Color.G,dlg.Color.R,c.Alpha)));};Row(label,b);
    }
    private void Header(string text)
    {
        var l=new Label{Text=text,AutoSize=true,Font=sectionFont,Margin=new Padding(0,14,0,8)};
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));var row=table.RowCount++;table.Controls.Add(l,0,row);table.SetColumnSpan(l,2);
    }
    private void Row(string label,Control value)
    {
        var row=table.RowCount++;table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label{Text=label,AutoSize=true,Margin=new Padding(0,6,6,5)},0,row);
        value.Dock=DockStyle.Fill;value.Margin=new Padding(0,3,0,4);table.Controls.Add(value,1,row);
    }
    private void Apply(string label,Action change) {if(!building) EditRequested?.Invoke(label,change);}
    private NumericUpDown MakeNumber(string label,double initial,Action<decimal> set,decimal min=-10000000,decimal max=10000000,int digits=3)
    {
        var safe=double.IsFinite(initial)?Math.Clamp((decimal)Math.Clamp(initial,(double)min,(double)max),min,max):0;
        var n=new NumericUpDown{DecimalPlaces=digits,Minimum=min,Maximum=max,Value=safe,ThousandsSeparator=false,Name="edit_"+label};
        n.Tag=safe;
        void Commit(){if(n.Value== (decimal)n.Tag)return;var v=n.Value;Apply("修改"+label,()=>set(v));n.Tag=v;}
        n.Validated+=(_,_)=>Commit();
        n.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Enter){Commit();e.SuppressKeyPress=true;}else if(e.KeyCode==Keys.Escape){n.Value=(decimal)n.Tag;e.SuppressKeyPress=true;}};
        return n;
    }
    private void Number(string label,double initial,Action<decimal> set,decimal min=-10000000,decimal max=10000000,int digits=3)
        =>Row(label,MakeNumber(label,initial,set,min,max,digits));
    private void Vector(string label,Vector3 initial,Action<int,float> set)
    {
        if(!wide){for(var i=0;i<3;i++){var axis=i;Number(label+" "+"XYZ"[i],Component(initial,i),v=>set(axis,(float)v));}return;}
        var fields=new TableLayoutPanel{ColumnCount=3,RowCount=2,AutoSize=true,Dock=DockStyle.Fill,Margin=Padding.Empty};
        for(var i=0;i<3;i++)
        {
            var axis=i;fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100f/3));
            fields.Controls.Add(new Label{Text="XYZ"[i].ToString(),AutoSize=true,Margin=new Padding(2,0,0,2)},i,0);
            var input=MakeNumber(label+" "+"XYZ"[i],Component(initial,i),v=>set(axis,(float)v));
            input.Dock=DockStyle.Fill;input.Margin=new Padding(1,0,3,0);fields.Controls.Add(input,i,1);
        }
        Row(label,fields);
    }
    private void Uv(string label,Vector2 initial,Action<Vector2> set)
    {
        Number(label+" U",initial.X,v=>set(new Vector2((float)v,initial.Y)));
        Number(label+" V",initial.Y,v=>set(new Vector2(initial.X,(float)v)));
    }
    private void TextField(string label,string initial,Action<string> set,bool multiline=false)
    {
        var t=new TextBox{Text=initial??"",Multiline=multiline,Height=multiline?68:24,Name="edit_"+label};var committed=t.Text;
        void Commit(){if(t.Text==committed)return;var v=t.Text;Apply("修改"+label,()=>set(v));committed=v;}
        t.Validated+=(_,_)=>Commit();t.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Escape){t.Text=committed;e.SuppressKeyPress=true;}
            else if(e.KeyCode==Keys.Enter && !multiline){Commit();e.SuppressKeyPress=true;}};Row(label,t);
    }
    private void Options(string label,string[] values,int selected,Action<int> set)
    {
        var combo=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList};combo.Items.AddRange(values);
        combo.SelectedIndex=Math.Clamp(selected,0,values.Length-1);combo.SelectedIndexChanged+=(_,_)=>{var index=combo.SelectedIndex;Apply(label,()=>set(index));};Row(label,combo);
    }
    private void ResourcePicker(ResourceType kind)
    {
        var resources=package.ResourceRequests.Where(r=>r.Type==kind || current.Type==ObjectType.MultiImage && r.Type==ResourceType.MultiImage).ToList();
        var combo=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList};combo.Items.Add("（无）");
        foreach(var r in resources)combo.Items.Add($"{r.Name??"未命名"} · 0x{r.ID:X8}");
        combo.SelectedIndex=resources.IndexOf(current.ResourceRequest)+1;
        combo.SelectedIndexChanged+=(_,_)=>{var r=combo.SelectedIndex>0?resources[combo.SelectedIndex-1]:null;Apply("更换资源",()=>current.ResourceRequest=r);};Row(kind==ResourceType.Font?"字体资源":"贴图资源",combo);
        if(kind!=ResourceType.Image)return;
        var picture=new PictureBox{Height=100,SizeMode=PictureBoxSizeMode.Zoom};
        try
        {
            var name=Path.GetFileNameWithoutExtension((current.ResourceRequest?.Name??"").Replace('\\','/'));
            var path=Directory.Exists(textureDirectory)?Directory.EnumerateFiles(textureDirectory,"*.png").FirstOrDefault(f=>
                string.Equals(Path.GetFileNameWithoutExtension(f),name,StringComparison.OrdinalIgnoreCase) ||
                f.Contains($"(0x{current.ResourceRequest?.ID:X8})",StringComparison.OrdinalIgnoreCase)):null;
            if(path!=null){using var img=System.Drawing.Image.FromFile(path);picture.Image=new Bitmap(img);}
        }
        catch(IOException){}catch(ArgumentException){}
        picture.Disposed+=(_,_)=>picture.Image?.Dispose();Row("PNG 预览",picture);
    }
    public static string TypeName(ObjectType type)=>type switch
    {ObjectType.String=>"文本",ObjectType.Group=>"组",ObjectType.Image=>"图像",ObjectType.SimpleImage=>"简单图像",ObjectType.ColoredImage=>"彩色图像",ObjectType.MultiImage=>"多图图像",_=>type.ToString()};
    public static float Angle(Quaternion q)=>MathF.Atan2(2*(q.W*q.Z+q.X*q.Y),1-2*(q.Y*q.Y+q.Z*q.Z))*180/MathF.PI;
    private static float Component(Vector3 v,int axis)=>axis==0?v.X:axis==1?v.Y:v.Z;
    private static Vector3 With(Vector3 v,int axis,float value)=>axis==0?new(value,v.Y,v.Z):axis==1?new(v.X,value,v.Z):new(v.X,v.Y,value);
    protected override void Dispose(bool disposing)
    {if(disposing)sectionFont.Dispose();base.Dispose(disposing);}
}

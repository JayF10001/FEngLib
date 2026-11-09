using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Design;
using System.Linq;
using System.Numerics;
using FEngLib.Structures;
using FEngViewer.TypeConverters;

namespace FEngViewer;

/// <summary>A property-grid adapter that localizes labels without changing model names or JSON.
/// Editor callbacks also capture mutations made inside modal collection editors.</summary>
internal sealed class LocalizedProperties:CustomTypeDescriptor
{
    private readonly object target;
    private readonly Action<Action> edit;
    public LocalizedProperties(object target,Action<Action> edit):base(TypeDescriptor.GetProvider(target).GetTypeDescriptor(target))
    {this.target=target;this.edit=edit;}
    public override object GetPropertyOwner(PropertyDescriptor pd)=>target;
    public override PropertyDescriptorCollection GetProperties()=>GetProperties(Array.Empty<Attribute>());
    public override PropertyDescriptorCollection GetProperties(Attribute[] attributes)=>new(
        base.GetProperties(attributes).Cast<PropertyDescriptor>().Select(p=>(PropertyDescriptor)new Property(p,target,edit)).ToArray());
    private sealed class Property:PropertyDescriptor
    {
        private readonly PropertyDescriptor inner;
        private readonly object target;
        private readonly Action<Action> edit;
        public Property(PropertyDescriptor inner,object target,Action<Action> edit):base(inner)
        {this.inner=inner;this.target=target;this.edit=edit;}
        public override string DisplayName=>EditorStrings.Translate(inner.DisplayName);
        public override string Category=>EditorStrings.Translate(inner.Category);
        public override string Description=>inner.Description;
        public override Type ComponentType=>inner.ComponentType;
        public override Type PropertyType=>inner.PropertyType;
        public override bool IsReadOnly=>inner.IsReadOnly;
        public override bool CanResetValue(object component)=>inner.CanResetValue(target);
        public override object GetValue(object component)=>inner.GetValue(target);
        public override void SetValue(object component,object value)
        {
            var finite=value switch
            {
                float f=>float.IsFinite(f),double d=>double.IsFinite(d),
                Vector2 v=>float.IsFinite(v.X)&&float.IsFinite(v.Y),
                Vector3 v=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z),
                Quaternion q=>float.IsFinite(q.X)&&float.IsFinite(q.Y)&&float.IsFinite(q.Z)&&float.IsFinite(q.W),_=>true
            };
            if(!finite)throw new ArgumentException("数值不能使用 NaN 或无穷值。");
            edit(()=>inner.SetValue(target,value));
        }
        public override void ResetValue(object component)=>edit(()=>inner.ResetValue(target));
        public override bool ShouldSerializeValue(object component)=>inner.ShouldSerializeValue(target);
        public override TypeConverter Converter=>PropertyType==typeof(Vector3)?new Vector3TypeConverter():
            PropertyType==typeof(Vector2)?new Vector2TypeConverter():PropertyType==typeof(Quaternion)?new QuaternionTypeConverter():
            PropertyType==typeof(Color4)?new Color4TypeConverter():inner.Converter;
        public override object GetEditor(Type editorBaseType)
        {
            var editor=inner.GetEditor(editorBaseType);
            return editor is UITypeEditor ui?new Editor(ui,edit):editor;
        }
    }
    private sealed class Editor:UITypeEditor
    {
        private readonly UITypeEditor inner;private readonly Action<Action> edit;
        public Editor(UITypeEditor inner,Action<Action> edit){this.inner=inner;this.edit=edit;}
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context)=>inner.GetEditStyle(context);
        public override object EditValue(ITypeDescriptorContext context,IServiceProvider provider,object value)
        {object result=value;edit(()=>result=inner.EditValue(context,provider,value));return result;}
        public override bool GetPaintValueSupported(ITypeDescriptorContext context)=>inner.GetPaintValueSupported(context);
        public override void PaintValue(PaintValueEventArgs e)=>inner.PaintValue(e);
    }
}

internal static class EditorStrings
{
    private static readonly Dictionary<string,string> Words=new()
    {
        ["File"]="文件",["Open FNG..."]="打开 FNG…",["Open container..."]="打开容器…",
        ["Open FNG folder..."]="加入 FNG 文件夹…",["Save"]="保存",["Save as..."]="另存为…",
        ["Save all modified FNG"]="保存全部修改",["Reload"]="重新加载",["Write back current container copy..."]="写回当前容器副本…",
        ["Close tab"]="关闭标签",["Close all tabs"]="关闭全部标签",["Cleanup and save"]="清理名称并另存为…",["Exit"]="退出",
        ["Rename"]="重命名…",["Move up"]="上移",["Move down"]="下移",["Duplicate"]="复制副本…",["Duplicate until..."]="批量复制…",
        ["Copy"]="复制",["Cut"]="剪切",["Paste"]="粘贴",["Delete"]="删除",["Order group"]="排序组",["Order group childs"]="递归排序组",
        ["Start"]="播放",["Stop"]="回到初始布局",["Background color"]="画布背景色…",
        ["Meta"]="标识",["Data"]="变换",["Flags"]="标志位",["Text"]="文本",["Image"]="图像",["Misc"]="其他",
        ["Name"]="名称",["Guid"]="GUID",["Resource"]="资源请求",["Color"]="颜色",["Pivot"]="旋转中心",["Position"]="位置",
        ["Rotation"]="旋转（四元数）",["Size"]="大小",["Invisible"]="不可见",["Type"]="类型",["Value"]="内容",
        ["Label"]="本地化标签",["Formatting"]="文本格式",["Leading"]="行距",["MaxWidth"]="最大宽度",["BufferLength"]="缓冲长度",
        ["Upper Left UV"]="左上 UV",["Lower Right UV"]="右下 UV",["FE Image Flags"]="图像标志",["Image (Colored)"]="四角颜色",
        ["Top Left Color"]="左上颜色",["Top Right Color"]="右上颜色",["Bottom Left Color"]="左下颜色",["Bottom Right Color"]="右下颜色",
        ["Hash (hex)"]="哈希（十六进制）",["Hash (dec)"]="哈希（十进制）",["BaseKey"]="基准键",["DeltaKeys"]="偏移键",
        ["InterpType"]="插值类型",["InterpAction"]="插值行为",["Length"]="时长",["Time"]="时间",["Val"]="键值",
        ["Id"]="ID",["ChainedId"]="后续脚本 ID",["ChainName"]="后续脚本名称",["Chain (hash)"]="后续脚本哈希",["Chain (dec)"]="后续脚本 ID",
        ["PlatformSpecific"]="平台限定",["MouseObject"]="鼠标对象",["SaveStaticTracks"]="保存静态轨道",["DontNavigate"]="不参与导航",
        ["UsesLibraryObject"]="使用库对象",["CodeSuppliedResource"]="由代码提供资源",["ObjectLocked"]="锁定对象",["HideInEdit"]="编辑器中隐藏",
        ["Resources"]="资源请求",["MessageResponse"]="消息响应",["MessageDefinitions"]="消息定义",["MessageTargets"]="消息目标",
        ["Browse..."]="浏览…",["Cancel"]="取消",["Extract Selected"]="提取选中项",["Extract All & Open Tabs"]="提取全部并打开标签",
        ["Container:"]="来源容器：",["Output folder:"]="输出文件夹：",["Filter:"]="筛选：",["Status"]="状态",
        ["Rename..."]="重命名…",["Background color..."]="画布背景色…",["Order group and children"]="递归排序组",
        ["Next"]="下一个",["Previous"]="上一个",["Start/Stop"]="播放/停止"
    };
    public static string Translate(string text)=>text!=null && Words.TryGetValue(text,out var result)?result:text;
}

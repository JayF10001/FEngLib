using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using FEngLib.Editing;
using FEngLib.Objects;
using FEngRender.Data;

namespace FEngViewer;

public partial class PackageView
{
    private ObjectInspector _inspector;
    private bool _simpleTree=true;
    private bool _restoringTree;
    private ToolStripMenuItem _undoItem,_redoItem;
    private readonly Dictionary<(WorkspaceDocument,bool),(string[] Expanded,string Top,string Selected)> _treeViews=new();
    private ToolStrip _canvasTools;
    private ToolStripComboBox _zoomChoice;
    private bool _syncingZoom;
    private readonly List<ToolStripButton> _toolButtons=new();

    private void InitializeEditorWorkbench()
    {
        _simpleTree=ViewerSettings.Load().SimpleTree;
        FileMenuItem.Text="文件(&F)";
        treeView1.HideSelection=false;
        treeView1.DrawMode=TreeViewDrawMode.OwnerDrawText;
        treeView1.DrawNode+=(_,e)=>
        {
            if(e.Node!=treeView1.SelectedNode) {e.DrawDefault=true;return;}
            using var background=new SolidBrush(EditorTheme.Accent);
            e.Graphics.FillRectangle(background,e.Bounds);
            TextRenderer.DrawText(e.Graphics,e.Node.Text,e.Node.NodeFont??treeView1.Font,e.Bounds,Color.White,
                TextFormatFlags.NoPrefix|TextFormatFlags.VerticalCenter);
        };
        EditorTheme.Dark=ViewerSettings.Load().DarkTheme;
        _inspector.EditRequested+=PerformEdit;
        _inspector.PlayRequested+=(obj,id)=>
        {if(viewOutput.IsPreviewOf(obj,id)&&!viewOutput.PreviewPlaying)viewOutput.ResumePreview();else viewOutput.StartPreview(obj,id);};
        _inspector.PauseRequested+=()=>viewOutput.PausePreview();
        _inspector.ResetRequested+=()=>{viewOutput.ResetPreview();_inspector.RefreshGeometry();};
        viewOutput.ObjectPicked+=node=>
        {
            if(node==null) {treeView1.SelectedNode=_rootNode;viewOutput.SelectedNode=null;return;}
            viewOutput.SelectedNode=node;
            var found=treeView1.Nodes.Find(PackageViewExtensions.GetObjectTreeKey(node.GetObject()),true).FirstOrDefault();
            if(found!=null)
            {
                SetLeftMode(WorkspaceLeftMode.ObjectTree);
                var wasVisible=found.IsVisible;
                treeView1.SelectedNode=found;
                found.EnsureVisible();
                if(!wasVisible)
                {
                    var top=found;
                    for(var i=0;i<Math.Max(1,treeView1.VisibleCount/2) && top.PrevVisibleNode!=null;i++)
                        top=top.PrevVisibleNode;
                    treeView1.TopNode=top;
                }
                treeView1.Invalidate();
            }
        };
        viewOutput.TransformFinished+=(obj,commit)=>
        {
            var document=_boundDocument??ActiveDocument;
            if(document?.History==null)return;
            if(!commit) document.History.CancelPending();
            else if(document.History.Commit("画布变换"))
                CurrentPackageWasModified(PackageViewExtensions.GetObjectTreeKey(obj));
        };
        viewOutput.TransformUpdated+=()=>_inspector.RefreshGeometry();
        viewOutput.EditorStatus+=message=>{_statusLeft.Text=message;};
        viewOutput.PreviewViewportChanged+=(_,_)=>
        {
            if(_zoomChoice==null || _zoomChoice.ComboBox.ContainsFocus)return;
            _syncingZoom=true;_zoomChoice.Text=viewOutput.Fit?"适应":$"{viewOutput.Zoom*100:0}%";_syncingZoom=false;
        };
        _workspace.StateChanged+=(_,_)=>UpdateHistoryButtons();
        _workspace.ActiveChanged+=(_,_)=>UpdateHistoryButtons();
        _workspace.Saving+=(_,_)=>{ValidateChildren();viewOutput.CancelInteraction();};

        var edit=new ToolStripMenuItem("编辑(&E)");
        _undoItem=new ToolStripMenuItem("撤销",null,(_,_)=>HistoryStep(false));
        _redoItem=new ToolStripMenuItem("重做",null,(_,_)=>HistoryStep(true));
        _undoItem.ShortcutKeyDisplayString="Ctrl+Z";_redoItem.ShortcutKeyDisplayString="Ctrl+Y";
        edit.DropDownItems.AddRange(new ToolStripItem[]{_undoItem,_redoItem});
        var objects=new ToolStripMenuItem("对象(&O)");
        objects.DropDownItems.Add(CreateMenuItem("新建对象…",Keys.Control|Keys.N,(_,_)=>CreateObject()));
        objects.DropDownItems.Add(CreateMenuItem("复制对象…",Keys.None,duplicateToolStripMenuItem_Click));
        objects.DropDownItems.Add(CreateMenuItem("批量复制…",Keys.None,duplicateUntilToolStripMenuItem_Click));
        objects.DropDownItems.Add(CreateMenuItem("重命名…",Keys.None,renameToolStripMenuItem_Click));
        objects.DropDownItems.Add(CreateMenuItem("删除",Keys.None,deleteToolStripMenuItem_Click));
        objects.DropDownOpening+=(_,_)=>
        {
            objects.DropDownItems[0].Enabled=_currentPackage!=null;
            foreach(ToolStripItem item in objects.DropDownItems.Cast<ToolStripItem>().Skip(1))
                item.Enabled=treeView1.SelectedNode?.Tag is RenderTreeNode;
        };
        var view=new ToolStripMenuItem("视图(&V)");
        var treeMode=new ToolStripMenuItem("简洁对象树"){CheckOnClick=true,Checked=_simpleTree};
        treeMode.CheckedChanged+=(_,_)=>SwitchTree(treeMode.Checked);view.DropDownItems.Add(treeMode);
        var dark=new ToolStripMenuItem("深色主题"){CheckOnClick=true,Checked=EditorTheme.Dark};
        dark.CheckedChanged+=(_,_)=>{EditorTheme.Dark=dark.Checked;ApplyEditorTheme();SaveLayoutState();};view.DropDownItems.Add(dark);
        view.DropDownItems.Add(CreateMenuItem("适应窗口",Keys.Control|Keys.D0,(_,_)=>viewOutput.FitToWindow()));
        view.DropDownItems.Add(CreateMenuItem("100%",Keys.Control|Keys.D1,(_,_)=>viewOutput.SetZoom(1)));
        var help=new ToolStripMenuItem("帮助(&H)");
        help.DropDownItems.Add("操作说明",null,(_,_)=>MessageBox.Show(this,
            "滚轮：鼠标中心缩放；中键：平移；Alt+点击：轮换重叠对象\n"+
            "V/W/R/E/H：选择/移动/缩放/旋转/平移；方向键：移动 1，Shift：10\n"+
            "角点等比缩放，边点单轴缩放；Shift+旋转：15°吸附\n"+
            "Esc：取消拖动或输入；Ctrl+Z/Y：撤销/重做\n"+
            "布局变换同步作用于全部已有脚本。开始编辑会回到 INIT 第 0 帧。\n"+
            "高级页可编辑原始字段。文件保存与容器写回不属于撤销范围。","操作说明"));
        menuStrip1.Items.AddRange(new ToolStripItem[]{edit,objects,view,help});
        BuildCanvasTools();
        var createContext=new ToolStripMenuItem("新建对象…",null,(_,_)=>CreateObject());
        objectContextMenu.Items.Insert(0,createContext);
        objectContextMenu.Opening+=(_,_)=>EditorTheme.Apply(objectContextMenu);
        KeyPreview=true;
        ApplyEditorTheme();UpdateHistoryButtons();
    }
    private void BuildCanvasTools()
    {
        _canvasTools=new ToolStrip{Dock=DockStyle.Top,GripStyle=ToolStripGripStyle.Hidden,AutoSize=true,
            CanOverflow=false,LayoutStyle=ToolStripLayoutStyle.Flow,Padding=new Padding(4,3,4,3)};
        ((FlowLayoutSettings)_canvasTools.LayoutSettings).WrapContents=true;
        foreach(var pair in new[]{("选择",CanvasTool.Select),("移动",CanvasTool.Move),("缩放",CanvasTool.Scale),("旋转",CanvasTool.Rotate),("平移",CanvasTool.Pan)})
        {
            var tool=pair.Item2;var b=new ToolStripButton(pair.Item1){Tag=tool,Checked=tool==viewOutput.Tool};
            b.Click+=(_,_)=>SetCanvasTool(tool);_toolButtons.Add(b);_canvasTools.Items.Add(b);
        }
        _canvasTools.Items.Add(new ToolStripSeparator());
        _canvasTools.Items.Add("适应",null,(_,_)=>viewOutput.FitToWindow());
        _canvasTools.Items.Add("100%",null,(_,_)=>viewOutput.SetZoom(1));
        var zoom=new ToolStripComboBox{Width=78,AutoSize=false};_zoomChoice=zoom;
        zoom.Items.AddRange(new object[]{"25%","50%","100%","200%","400%","800%"});
        zoom.Text="适应";
        zoom.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Enter && float.TryParse(zoom.Text.TrimEnd('%'),out var v)){viewOutput.SetZoom(v/100);e.SuppressKeyPress=true;}};
        zoom.SelectedIndexChanged+=(_,_)=>{if(!_syncingZoom && float.TryParse(zoom.Text.TrimEnd('%'),out var v))viewOutput.SetZoom(v/100);};
        _canvasTools.Items.Add(zoom);
        _canvasTools.Items.Add(new ToolStripSeparator());
        foreach(var label in new[]{"网格","外框","名称","组范围"})
        {
            var b=new ToolStripButton(label){CheckOnClick=true};b.CheckedChanged+=(_,_)=>
            {if(label=="网格")viewOutput.ShowGrid=b.Checked;else if(label=="外框")viewOutput.ShowOutlines=b.Checked;else if(label=="名称")viewOutput.ShowNames=b.Checked;else viewOutput.ShowGroups=b.Checked;};
            _canvasTools.Items.Add(b);
        }
        _innerSplit.Panel1.Controls.Add(_canvasTools);_canvasTools.SendToBack();
    }
    private void SetCanvasTool(CanvasTool tool)
    {viewOutput.CancelInteraction();viewOutput.ResetPreview();viewOutput.Tool=tool;foreach(var b in _toolButtons)b.Checked=Equals(b.Tag,tool);}
    private void CreateObject()
    {
        if(_currentPackage==null)return;
        ValidateChildren();viewOutput.CancelInteraction();
        using var dialog=new NewFrontendObjectDialog(_currentPackage,viewOutput.SelectedNode?.GetObject());
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        PerformEdit("新建"+ObjectInspector.TypeName(dialog.Kind),()=>
        {
            var obj=FrontendObjectFactory.Create(_currentPackage,dialog.Kind,dialog.ObjectName,dialog.ParentObject,dialog.Resource);
            ActiveDocument.SelectedGuid=obj.Guid;
        });
    }
    private void PerformEdit(string label,Action mutation)
    {
        var document=_boundDocument ?? ActiveDocument;
        if(document?.History==null)return;
        var history=document.History;
        try
        {
            viewOutput.ResetPreview();mutation();history.Commit(label);
            var obj=_currentPackage.Objects.FirstOrDefault(o=>o.Guid==document.SelectedGuid);
            CurrentPackageWasModified(PackageViewExtensions.GetObjectTreeKey(obj));
        }
        catch(Exception ex)
        {
            history.CancelPending();viewOutput.PrepareLayout();
            MessageBox.Show(this,ex.Message,"无法完成编辑",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }
    }
    private void HistoryStep(bool redo)
    {
        ValidateChildren();viewOutput.CancelInteraction();viewOutput.ResetPreview();
        var doc=ActiveDocument;if(doc?.History==null)return;
        if(!(redo?doc.History.Redo():doc.History.Undo()))return;
        doc.SyncHistoryState();
        var obj=_currentPackage.Objects.FirstOrDefault(o=>o.Guid==doc.SelectedGuid);
        CurrentPackageWasModified(PackageViewExtensions.GetObjectTreeKey(obj));
        _workspace.NotifyStateChanged();
    }
    private void UpdateHistoryButtons()
    {
        if(_undoItem==null)return;
        var h=ActiveDocument?.History;
        if(_canvasTools!=null)_canvasTools.Enabled=ActiveDocument?.Package!=null;
        _undoItem.Enabled=h?.CanUndo==true;_redoItem.Enabled=h?.CanRedo==true;
        _undoItem.Text="撤销"+(h?.CanUndo==true?" · "+h.UndoLabel:"");
        _redoItem.Text="重做"+(h?.CanRedo==true?" · "+h.RedoLabel:"");
    }
    private void SwitchTree(bool simple)
    {
        if(_restoringTree || _simpleTree==simple)return;
        RememberTreeView();_simpleTree=simple;
        if(_currentPackage!=null){PopulateTreeView(_currentPackage,_currentRenderTree);RestoreTreeView();}
        SaveLayoutState();
    }
    private void RememberTreeView()
    {
        var document=_boundDocument;
        if(document==null || treeView1.Nodes.Count==0)return;
        _treeViews[(document,_simpleTree)]=(FlattenTree(treeView1).Where(n=>n.IsExpanded).Select(n=>n.Name).ToArray(),treeView1.TopNode?.Name,treeView1.SelectedNode?.Name);
    }
    private void RestoreTreeView()
    {
        if(ActiveDocument==null)return;
        var wanted=ActiveDocument.SelectedGuid;
        if(_treeViews.TryGetValue((ActiveDocument,_simpleTree),out var state))
        {
            _restoringTree=true;
            foreach(var n in FlattenTree(treeView1))if(state.Expanded.Contains(n.Name))n.Expand();
            var previous=string.IsNullOrEmpty(state.Selected)?null:treeView1.Nodes.Find(state.Selected,true).FirstOrDefault();
            if(previous!=null && FindTreeOwner(previous)?.GetObject().Guid==wanted) RestoreTreeSelection(state.Selected);
            if(!string.IsNullOrEmpty(state.Top))
            {var top=treeView1.Nodes.Find(state.Top,true).FirstOrDefault();if(top!=null)treeView1.TopNode=top;}
            _restoringTree=false;
        }
        var obj=_currentPackage.Objects.FirstOrDefault(o=>o.Guid==wanted);
        if(obj!=null && FindTreeOwner(treeView1.SelectedNode)?.GetObject().Guid!=wanted)
            RestoreTreeSelection(PackageViewExtensions.GetObjectTreeKey(obj));
    }
    private static RenderTreeNode FindTreeOwner(TreeNode node)
    {for(var n=node;n!=null;n=n.Parent)if(n.Tag is RenderTreeNode owner)return owner;return null;}
    private void ApplyEditorTheme()
    {
        EditorTheme.Apply(this);EditorTheme.Apply(objectContextMenu);EditorTheme.Apply(scriptContextMenu);EditorTheme.Apply(viewerContextMenu);
        if(_workspaceMenu!=null)EditorTheme.Apply(_workspaceMenu);
        _tabBar?.Invalidate();
        SetLeftMode(_leftMode);
    }
    private bool _gridRefreshQueued;
    private void EditAdvanced(Action mutation)
    {
        var document=_boundDocument??ActiveDocument;if(document?.History==null)return;
        viewOutput.ResetPreview();
        try {mutation();document.History.Commit("编辑高级属性");document.SyncHistoryState();}
        catch {document.History.CancelPending();throw;}
        if(_gridRefreshQueued || !IsHandleCreated)return;
        _gridRefreshQueued=true;
        BeginInvoke(new Action(()=>
        {
            _gridRefreshQueued=false;
            if(!ReferenceEquals(ActiveDocument,document))return;
            CurrentPackageWasModified(treeView1.SelectedNode?.Name);
        }));
    }
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
    {
        var focused=FindFocused(this);
        var typing=focused is TextBoxBase or NumericUpDown or ComboBox || focused?.Parent is NumericUpDown;
        if(!typing && keyData==(Keys.Control|Keys.Z)){HistoryStep(false);return true;}
        if(!typing && (keyData==(Keys.Control|Keys.Y) || keyData==(Keys.Control|Keys.Shift|Keys.Z))){HistoryStep(true);return true;}
        if(!typing && keyData==Keys.Escape){viewOutput.CancelInteraction();return true;}
        if(!typing && _currentPackage!=null)
        {
            if(viewOutput.ContainsFocus || treeView1.ContainsFocus)
            {
                if(keyData==(Keys.Control|Keys.C)){copyToolStripMenuItem_Click(this,EventArgs.Empty);return true;}
                if(keyData==(Keys.Control|Keys.X)){cutToolStripMenuItem_Click(this,EventArgs.Empty);return true;}
                if(keyData==(Keys.Control|Keys.V)){pasteToolStripMenuItem_Click(this,EventArgs.Empty);return true;}
                if(keyData==Keys.Delete){deleteToolStripMenuItem_Click(this,EventArgs.Empty);return true;}
            }
            if(keyData is Keys.V or Keys.W or Keys.R or Keys.E or Keys.H)
            {SetCanvasTool(keyData==Keys.V?CanvasTool.Select:keyData==Keys.W?CanvasTool.Move:keyData==Keys.R?CanvasTool.Scale:keyData==Keys.E?CanvasTool.Rotate:CanvasTool.Pan);return true;}
            var code=keyData&Keys.KeyCode;
            if(viewOutput.ContainsFocus && code is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
            {
                var node=viewOutput.SelectedNode;
                if(node!=null && node.PoseError==null && !node.GetObject().Flags.HasFlag(ObjectFlags.ObjectLocked))
                {
                    var step=(keyData&Keys.Shift)!=0?10:1;
                    var delta=new Vector3(code==Keys.Left?-step:code==Keys.Right?step:0,code==Keys.Up?-step:code==Keys.Down?step:0,0);
                    PerformEdit("微调位置",()=>
                    {
                        if(!RenderGeometry.TryParentPlaneInverse(node,out var inverse))throw new InvalidOperationException("父变换不可逆。");
                        var local=Vector3.TransformNormal(delta,inverse);local.Z=0;
                        ObjectPropertyEditing.SetPosition(node.GetObject(),node.GetObject().Data.Position+local);
                    });
                }return true;
            }
            if(viewOutput.ContainsFocus && keyData==Keys.Delete){deleteToolStripMenuItem_Click(this,EventArgs.Empty);return true;}
        }
        // Menu shortcuts for copy/cut/delete must not steal native text editing.
        if(typing && keyData is (Keys.Control|Keys.C) or (Keys.Control|Keys.V) or (Keys.Control|Keys.X) or Keys.Delete)
            return false;
        return base.ProcessCmdKey(ref msg,keyData);
    }
    private static Control FindFocused(Control root)
    {foreach(Control c in root.Controls){if(c.Focused)return c;if(c.ContainsFocus)return FindFocused(c);}return null;}
}

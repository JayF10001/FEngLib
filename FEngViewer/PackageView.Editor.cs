using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using FEngLib.Editing;
using FEngLib.Objects;
using FEngRender.Data;
using FEngRender.Utils;

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
    private ToolStripComboBox _backdropChoice;
    private WorkspaceBackdrop _workspaceBackdrop = WorkspaceBackdrop.DeepGray;
    private bool _syncingZoom;
    private readonly List<ToolStripButton> _toolButtons=new();
    private ToolStripButton _packagePlayButton,_packagePauseButton,_packageResetButton;
    private ToolStripSeparator _editorToolSeparator;
    private readonly List<ToolStripItem> _packagePreviewTools=new();
    private string _cutTreeKey;

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
        _inspector.EditRequested+=PerformPropertyEdit;
        _inspector.PlayRequested+=(obj,id)=>
        {if(viewOutput.IsPreviewOf(obj,id)&&!viewOutput.PreviewPlaying)viewOutput.ResumePreview();else viewOutput.StartPreview(obj,id);};
        _inspector.PauseRequested+=()=>viewOutput.PausePreview();
        _inspector.ResetRequested+=()=>{viewOutput.ResetPreview();_inspector.RefreshGeometry();};
        _inspector.TextureSourceRequested+=(obj,request)=>ShowTextureSource(obj,request);
        _inspector.TextureReplaceRequested+=(obj,request)=>ShowTextureReplace(obj,request);
        viewOutput.ObjectPicked+=node=>
        {
            if (_boundDocument == null) return;
            if(node==null) {ClearObjectSelection();return;}
            viewOutput.SelectedNode=node;
            var found=treeView1.Nodes.Find(PackageViewExtensions.GetObjectTreeKey(node.GetObject()),true).FirstOrDefault();
            if(found!=null)
            {
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
            {
                _workspace.MarkDirty(document);
                RefreshAfterInPlaceEdit(document);
            }
        };
        viewOutput.TransformUpdated+=()=>_inspector.RefreshGeometry();
        viewOutput.EditorStatus+=message=>{_statusLeft.Text=message;};
        viewOutput.PreviewViewportChanged+=(_,_)=>
        {
            if(_zoomChoice==null || _zoomChoice.ComboBox.ContainsFocus)return;
            _syncingZoom=true;_zoomChoice.Text=viewOutput.Fit?"适应":$"{viewOutput.Zoom*100:0}%";_syncingZoom=false;
        };
        viewOutput.PreviewStateChanged+=UpdatePackagePreviewControls;
        _workspace.StateChanged+=(_,_)=>UpdateHistoryButtons();
        _workspace.ActiveChanged+=(_,_)=>UpdateHistoryButtons();
        _workspace.Saving+=(_,_)=>{ValidateChildren();viewOutput.CancelInteraction();};

        var edit=new ToolStripMenuItem("编辑(&E)");
        _undoItem=new ToolStripMenuItem("撤销",null,(_,_)=>HistoryStep(false));
        _redoItem=new ToolStripMenuItem("重做",null,(_,_)=>HistoryStep(true));
        _undoItem.ShortcutKeyDisplayString="Ctrl+Z";_redoItem.ShortcutKeyDisplayString="Ctrl+Y";
        ToolStripMenuItem ShortcutItem(string text,string display,EventHandler handler)
        {
            var item=new ToolStripMenuItem(text,null,handler){ShortcutKeyDisplayString=display};
            return item;
        }
        edit.DropDownItems.AddRange(new ToolStripItem[]
        {
            _undoItem,_redoItem,new ToolStripSeparator(),
            ShortcutItem("复制对象","Ctrl+C",copyToolStripMenuItem_Click),
            ShortcutItem("剪切对象","Ctrl+X",cutToolStripMenuItem_Click),
            ShortcutItem("粘贴对象","Ctrl+V",pasteToolStripMenuItem_Click),
            ShortcutItem("删除对象","Backspace",deleteToolStripMenuItem_Click)
        });
        edit.DropDownOpening+=(_,_)=>
        {
            var hasObject=SelectedObjectNode()!=null;
            foreach(ToolStripItem item in edit.DropDownItems.Cast<ToolStripItem>().Skip(2))
                if(item is not ToolStripSeparator) item.Enabled=hasObject;
        };
        // The context menu used to own real ShortcutKeys; that steals Ctrl+C/V/X from text
        // fields. ProcessCmdKey handles them only outside text editing, so keep display-only.
        copyToolStripMenuItem.ShortcutKeys=Keys.None;copyToolStripMenuItem.ShortcutKeyDisplayString="Ctrl+C";
        cutToolStripMenuItem.ShortcutKeys=Keys.None;cutToolStripMenuItem.ShortcutKeyDisplayString="Ctrl+X";
        pasteToolStripMenuItem.ShortcutKeys=Keys.None;pasteToolStripMenuItem.ShortcutKeyDisplayString="Ctrl+V";
        deleteToolStripMenuItem.ShortcutKeys=Keys.None;deleteToolStripMenuItem.ShortcutKeyDisplayString="Backspace";
        var objects=new ToolStripMenuItem("对象(&O)");
        var newObjectItem=CreateMenuItem("新建对象…",Keys.Control|Keys.N,(_,_)=>CreateObject());
        newObjectItem.ShortcutKeyDisplayString="Ctrl+N";objects.DropDownItems.Add(newObjectItem);
        objects.DropDownItems.Add(CreateMenuItem("复制对象…",Keys.None,duplicateToolStripMenuItem_Click));
        objects.DropDownItems.Add(CreateMenuItem("批量复制…",Keys.None,duplicateUntilToolStripMenuItem_Click));
        var renameItem=CreateMenuItem("重命名…",Keys.None,renameToolStripMenuItem_Click);
        renameItem.ShortcutKeyDisplayString="Ctrl+R";objects.DropDownItems.Add(renameItem);
        var deleteItem=CreateMenuItem("删除",Keys.None,deleteToolStripMenuItem_Click);
        deleteItem.ShortcutKeyDisplayString="Backspace";objects.DropDownItems.Add(deleteItem);
        objects.DropDownItems.Add(new ToolStripSeparator());
        objects.DropDownItems.Add(ShortcutItem("复制对象到剪贴板","Ctrl+C",copyToolStripMenuItem_Click));
        objects.DropDownItems.Add(ShortcutItem("剪切对象","Ctrl+X",cutToolStripMenuItem_Click));
        objects.DropDownItems.Add(ShortcutItem("粘贴对象","Ctrl+V",pasteToolStripMenuItem_Click));
        objects.DropDownItems.Add(new ToolStripSeparator());
        objects.DropDownItems.Add(CreateMenuItem("移动到组…",Keys.None,(_,_)=>MoveSelectedObjectToGroup()));
        objects.DropDownOpening+=(_,_)=>
        {
            objects.DropDownItems[0].Enabled=_currentPackage!=null;
            var hasObject=SelectedObjectNode()!=null;
            foreach(ToolStripItem item in objects.DropDownItems.Cast<ToolStripItem>().Skip(1))
                item.Enabled=hasObject;
        };
        var view=new ToolStripMenuItem("视图(&V)");
        var treeMode=new ToolStripMenuItem("简洁对象树"){CheckOnClick=true,Checked=_simpleTree};
        treeMode.CheckedChanged+=(_,_)=>SwitchTree(treeMode.Checked);view.DropDownItems.Add(treeMode);
        var dark=new ToolStripMenuItem("深色主题"){CheckOnClick=true,Checked=EditorTheme.Dark};
        dark.CheckedChanged+=(_,_)=>{EditorTheme.Dark=dark.Checked;ApplyEditorTheme();SaveLayoutState();};view.DropDownItems.Add(dark);
        view.DropDownItems.Add(CreateMenuItem("适应窗口",Keys.Control|Keys.D0,(_,_)=>viewOutput.FitToWindow()));
        view.DropDownItems.Add(CreateMenuItem("100%",Keys.Control|Keys.D1,(_,_)=>viewOutput.SetZoom(1)));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(CreateMenuItem("贴图来源…",Keys.None,(_,_)=>ConfigureTextureSources()));
        view.DropDownItems.Add(_containerIndexMenu);
        var help=new ToolStripMenuItem("帮助(&H)");
        help.DropDownItems.Add("操作说明",null,(_,_)=>MessageBox.Show(this,
            "滚轮：鼠标中心缩放；中键：平移；Alt+点击：轮换重叠对象\n"+
            "V/W/R/E/H：选择/移动/缩放/旋转/平移；方向键：移动 1，Shift：10\n"+
            "Ctrl+C/X/V：复制/剪切/粘贴；Backspace：删除；对象树可直接拖到目标组\n"+
            "角点等比缩放，边点单轴缩放；Shift+旋转：15°吸附\n"+
            "Esc：取消拖动或输入；Ctrl+Z/Y：撤销/重做\n"+
            "布局变换同步作用于全部已有脚本。开始编辑会回到 INIT 第 0 帧。\n"+
            "高级页可编辑原始字段。文件保存与容器写回不属于撤销范围。","操作说明"));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add("关于 FEngEditor…",null,(_,_)=>ShowAbout());
        menuStrip1.Items.AddRange(new ToolStripItem[]{edit,objects,view,_toolsMenu,help});
        BuildCanvasTools();
        var createContext=new ToolStripMenuItem("新建对象…",null,(_,_)=>CreateObject());
        objectContextMenu.Items.Insert(0,createContext);
        objectContextMenu.Items.Add(CreateMenuItem("移动到组…",Keys.None,(_,_)=>MoveSelectedObjectToGroup()));
        objectContextMenu.Opening+=(_,_)=>EditorTheme.Apply(objectContextMenu);
        treeView1.AllowDrop=true;
        treeView1.ItemDrag+=TreeItemDrag;
        treeView1.DragEnter+=TreeDragEnter;
        treeView1.DragOver+=TreeDragOver;
        treeView1.DragDrop+=TreeDragDrop;
        KeyPreview=true;
        ApplyEditorTheme();UpdateHistoryButtons();
    }
    private void BuildCanvasTools()
    {
        _canvasTools=new ToolStrip{Dock=DockStyle.Top,GripStyle=ToolStripGripStyle.Hidden,AutoSize=true,
            CanOverflow=false,LayoutStyle=ToolStripLayoutStyle.Flow,Padding=new Padding(4,3,4,3)};
        ((FlowLayoutSettings)_canvasTools.LayoutSettings).WrapContents=true;
        _packagePlayButton=new ToolStripButton("全体预览 30 秒",null,(_,_)=>viewOutput.StartPackagePreview(DisplayedPackage));
        _packagePauseButton=new ToolStripButton("暂停",null,(_,_)=>
        {if(viewOutput.PreviewPlaying)viewOutput.PausePreview();else viewOutput.ResumePreview();});
        _packageResetButton=new ToolStripButton("初始布局",null,(_,_)=>viewOutput.ResetPreview());
        _packagePreviewTools.AddRange(new ToolStripItem[]{_packagePlayButton,_packagePauseButton,_packageResetButton,new ToolStripSeparator()});
        _canvasTools.Items.AddRange(_packagePreviewTools.ToArray());
        foreach(var pair in new[]{("选择",CanvasTool.Select),("移动",CanvasTool.Move),("缩放",CanvasTool.Scale),("旋转",CanvasTool.Rotate),("平移",CanvasTool.Pan)})
        {
            var tool=pair.Item2;var b=new ToolStripButton(pair.Item1){Tag=tool,Checked=tool==viewOutput.Tool};
            b.Click+=(_,_)=>SetCanvasTool(tool);_toolButtons.Add(b);_canvasTools.Items.Add(b);
        }
        _editorToolSeparator=new ToolStripSeparator();
        _canvasTools.Items.Add(_editorToolSeparator);
        _canvasTools.Items.Add("适应",null,(_,_)=>viewOutput.FitToWindow());
        _canvasTools.Items.Add("100%",null,(_,_)=>viewOutput.SetZoom(1));
        var zoom=new ToolStripComboBox{Width=78,AutoSize=false};_zoomChoice=zoom;
        zoom.Items.AddRange(new object[]{"25%","50%","100%","200%","400%","800%"});
        zoom.Text="适应";
        zoom.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Enter && float.TryParse(zoom.Text.TrimEnd('%'),out var v)){viewOutput.SetZoom(v/100);e.SuppressKeyPress=true;}};
        zoom.SelectedIndexChanged+=(_,_)=>{if(!_syncingZoom && float.TryParse(zoom.Text.TrimEnd('%'),out var v))viewOutput.SetZoom(v/100);};
        _canvasTools.Items.Add(zoom);
        _canvasTools.Items.Add(new ToolStripSeparator());
        _canvasTools.Items.Add(new ToolStripLabel("背景"));
        _backdropChoice=new ToolStripComboBox{Name="canvasBackdropCombo",AccessibleName="背景",
            AutoSize=false,Width=Math.Max(78,LogicalToDeviceUnits(92)),
            DropDownStyle=ComboBoxStyle.DropDownList,
            ToolTipText="工作区背景；实际 FNG 画布固定为黑色"};
        foreach(var backdrop in new[]{WorkspaceBackdrop.DeepGray,WorkspaceBackdrop.Black,WorkspaceBackdrop.Checkerboard})
            _backdropChoice.Items.Add(CanvasBackdrop.DisplayName(backdrop));
        _backdropChoice.SelectedIndex=0;
        _backdropChoice.SelectedIndexChanged+=(_,_)=>ApplyWorkspaceBackdrop();
        _canvasTools.Items.Add(_backdropChoice);
        _canvasTools.Items.Add(new ToolStripSeparator());
        foreach(var label in new[]{"网格","外框","名称","组范围"})
        {
            var b=new ToolStripButton(label){CheckOnClick=true};b.CheckedChanged+=(_,_)=>
            {if(label=="网格")viewOutput.ShowGrid=b.Checked;else if(label=="外框")viewOutput.ShowOutlines=b.Checked;else if(label=="名称")viewOutput.ShowNames=b.Checked;else viewOutput.ShowGroups=b.Checked;};
            _canvasTools.Items.Add(b);
        }
        _innerSplit.Panel1.Controls.Add(_canvasTools);_canvasTools.SendToBack();
        UpdatePackagePreviewControls();
    }
    private void ApplyWorkspaceBackdrop()
    {
        if(_backdropChoice==null)return;
        _workspaceBackdrop=_backdropChoice.SelectedIndex switch
        {
            1=>WorkspaceBackdrop.Black,
            2=>WorkspaceBackdrop.Checkerboard,
            _=>WorkspaceBackdrop.DeepGray
        };
        viewOutput.Backdrop=_workspaceBackdrop;
    }
    private void UpdatePackagePreviewControls()
    {
        if(_packagePlayButton==null)return;
        var files=_leftMode==WorkspaceLeftMode.Files;
        foreach(var item in _packagePreviewTools)item.Visible=files;
        foreach(var item in _toolButtons)item.Visible=!files;
        if(_editorToolSeparator!=null)_editorToolSeparator.Visible=!files;
        _packagePlayButton.Text=viewOutput.PackagePreviewActive?"重新预览 30 秒":"全体预览 30 秒";
        _packagePlayButton.Enabled=DisplayedPackage!=null;
        _packagePauseButton.Enabled=viewOutput.PackagePreviewActive;
        _packagePauseButton.Text=viewOutput.PreviewPlaying?"暂停":"继续";
        _packageResetButton.Enabled=viewOutput.PackagePreviewActive;
        _packagePlayButton.ToolTipText="每个对象播放一个展示型脚本；隐藏、离场等脚本不自动播放。";
        if (_workspacePause != null)
        {
            _workspacePause.Enabled = viewOutput.PackagePreviewActive;
            _workspacePause.Text = viewOutput.PreviewPlaying ? "暂停" : "继续";
        }
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

    /// <summary>
    /// Ordinary inspector edits only change one object's data/flags. Re-evaluating the existing
    /// render tree is enough; rebuilding the tree and property panel loses focus, scroll and makes
    /// every checkbox click look like a refresh.
    /// </summary>
    private void PerformPropertyEdit(string label,Action mutation)
    {
        var document=_boundDocument ?? ActiveDocument;
        if(document?.History==null)return;
        var history=document.History;
        try
        {
            viewOutput.ResetPreview();
            mutation();
            history.Commit(label);
            _workspace.MarkDirty(document);
            RefreshAfterInPlaceEdit(document, label.Contains("资源",StringComparison.Ordinal));
        }
        catch(Exception ex)
        {
            history.CancelPending();viewOutput.PrepareLayout();
            MessageBox.Show(this,ex.Message,"无法完成编辑",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }
    }

    /// <summary>Re-evaluate the current tree and repaint without rebuilding tree nodes or controls.</summary>
    private void RefreshAfterInPlaceEdit(WorkspaceDocument document,bool refreshInspector=false)
    {
        RenderGeometry.Prepare(_currentRenderTree);
        RefreshSelectedTreeNodeText();
        Render();
        SetFormDetails();
        if(refreshInspector)_inspector?.RefreshAll();
    }

    private void RefreshSelectedTreeNodeText()
    {
        if(treeView1.SelectedNode?.Tag is not RenderTreeNode node)return;
        var obj=node.GetObject();
        var text=PackageViewExtensions.GetObjectText(obj)
            +(obj.Flags.HasFlag(ObjectFlags.Invisible)?"（隐藏）":"");
        if(treeView1.SelectedNode.Text==text)return;
        treeView1.SelectedNode.Text=text;
        treeView1.Invalidate();
    }
    private void HistoryStep(bool redo)
    {
        if (ActiveDocument == null) return;
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
        if(_canvasTools!=null)_canvasTools.Enabled=DisplayedPackage!=null;
        UpdatePackagePreviewControls();
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
        ApplyStartPageTheme();
        if(_workspaceMenu!=null)EditorTheme.Apply(_workspaceMenu);
        _tabBar?.Invalidate();
        SetLeftMode(_leftMode);
    }
    private void EditAdvanced(Action mutation)
    {
        var document=_boundDocument??ActiveDocument;if(document?.History==null)return;
        viewOutput.ResetPreview();
        try
        {
            mutation();
            document.History.Commit("编辑高级属性");
            document.SyncHistoryState();
            _workspace.MarkDirty(document);
            RefreshAfterInPlaceEdit(document);
        }
        catch
        {
            document.History.CancelPending();
            throw;
        }
    }
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
    {
        var focused=FindFocused(this);
        var typing=focused is TextBoxBase or NumericUpDown or ComboBox || focused?.Parent is NumericUpDown;
        if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab))
        {
            var pages = _workspace.DocumentCount + 1;
            var current = _workspace.IndexOfDocument(ActiveDocument) + 1;
            var next = (current + ((keyData & Keys.Shift) != 0 ? pages - 1 : 1)) % pages;
            if (next == 0) _workspace.ShowWorkspace();
            else _workspace.Activate(_workspace.Documents[next - 1]);
            return true;
        }
        if(!typing && keyData==(Keys.Control|Keys.Z)){HistoryStep(false);return true;}
        if(!typing && (keyData==(Keys.Control|Keys.Y) || keyData==(Keys.Control|Keys.Shift|Keys.Z))){HistoryStep(true);return true;}
        if(!typing && keyData==Keys.Escape){viewOutput.CancelInteraction();return true;}
        if(!typing && _currentPackage!=null)
        {
            if(keyData==(Keys.Control|Keys.V) && _packageViewExtensions.ObjectSelected!=null)
            {
                pasteToolStripMenuItem_Click(this,EventArgs.Empty);return true;
            }
            if(SelectedObjectNode()!=null)
            {
                if(keyData==(Keys.Control|Keys.C)){copyToolStripMenuItem_Click(this,EventArgs.Empty);return true;}
                if(keyData==(Keys.Control|Keys.X)){cutToolStripMenuItem_Click(this,EventArgs.Empty);return true;}
                if(keyData==Keys.Back){deleteToolStripMenuItem_Click(this,EventArgs.Empty);return true;}
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
            if(viewOutput.ContainsFocus && keyData==Keys.Back){deleteToolStripMenuItem_Click(this,EventArgs.Empty);return true;}
        }
        // Menu shortcuts for copy/cut/delete must not steal native text editing.
        if(typing && keyData is (Keys.Control|Keys.C) or (Keys.Control|Keys.V) or (Keys.Control|Keys.X) or Keys.Delete or Keys.Back)
            return false;
        return base.ProcessCmdKey(ref msg,keyData);
    }
    private static Control FindFocused(Control root)
    {foreach(Control c in root.Controls){if(c.Focused)return c;if(c.ContainsFocus)return FindFocused(c);}return null;}
    private void MarkCutObject(IObject<ObjectData> obj)
    {
        ClearCutMarker();
        if (obj == null) return;
        _cutTreeKey = PackageViewExtensions.GetObjectTreeKey(obj);
        var node = treeView1.Nodes.Find(_cutTreeKey, true).FirstOrDefault();
        if (node == null) return;
        node.Text = "✂ " + node.Text;
        node.ForeColor = Color.Gray;
        treeView1.Invalidate();
    }

    private void ClearCutMarker()
    {
        if (_cutTreeKey == null) return;
        var node = treeView1.Nodes.Find(_cutTreeKey, true).FirstOrDefault();
        if (node != null && node.Text.StartsWith("✂ ", StringComparison.Ordinal))
        {
            node.Text = node.Text[2..];
            node.ForeColor = treeView1.ForeColor;
            treeView1.Invalidate();
        }
        _cutTreeKey = null;
    }

    private RenderTreeNode SelectedObjectNode()
    {
        var treeNode=treeView1.SelectedNode?.Tag as RenderTreeNode;
        return viewOutput.ContainsFocus
            ? (viewOutput.SelectedNode ?? treeNode)
            : (treeNode ?? viewOutput.SelectedNode);
    }

    private void MoveSelectedObjectToGroup()
    {
        if (_currentPackage == null || _currentRenderTree == null) return;
        var node = SelectedObjectNode();
        if (node == null) return;
        if (node.PoseError != null)
        {
            MessageBox.Show(this, "对象当前布局无法求值，无法换算父级变换：\n" + node.PoseError,
                "移动到组", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var obj = node.GetObject();
        using var dialog = new MoveToGroupDialog(_currentPackage, obj);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        MoveObjectToGroup(obj, dialog.SelectedParent, "移动到组");
    }

    private void MoveObjectToGroup(IObject<ObjectData> obj, IObject<ObjectData> target, string label)
    {
        if (_currentPackage == null || _currentRenderTree == null || obj == null) return;
        if (ReferenceEquals(obj.Parent, target)) return;
        if (target != null && target is not Group)
        {
            _statusLeft.Text = "只能移动到 Group 或根级。";
            return;
        }

        var node = FindRenderNode(_currentRenderTree, obj.Guid);
        if (node?.PoseError != null)
        {
            MessageBox.Show(this, "对象当前布局无法求值，无法换算父级变换：\n" + node.PoseError,
                label, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var document = _boundDocument ?? ActiveDocument;
        if (document != null) document.SelectedGuid = obj.Guid;

        PerformEdit(label, () =>
        {
            RenderGeometry.Prepare(_currentRenderTree);
            var oldParentNode = obj.Parent == null ? null : FindRenderNode(_currentRenderTree, obj.Parent.Guid);
            var newParentNode = target == null ? null : FindRenderNode(_currentRenderTree, target.Guid);
            if (obj.Parent != null && oldParentNode == null)
                throw new InvalidOperationException("找不到旧父级的渲染节点，无法计算父变换。");
            if (target != null && newParentNode == null)
                throw new InvalidOperationException("找不到目标组的渲染节点，无法计算父变换。");
            var oldParentWorld = oldParentNode?.Transform ?? Matrix4x4.Identity;
            var newParentWorld = newParentNode?.Transform ?? Matrix4x4.Identity;
            ObjectHierarchyEditing.MoveToGroup(_currentPackage, obj, target, oldParentWorld, newParentWorld);
        });
    }

    private void TreeItemDrag(object sender, ItemDragEventArgs e)
    {
        if (e.Item is TreeNode node && node.Tag is RenderTreeNode)
            treeView1.DoDragDrop(node, DragDropEffects.Move);
    }

    private bool TryGetDropTarget(DragEventArgs e, out RenderTreeNode target, out bool root)
    {
        target = null;
        root = false;
        var point = treeView1.PointToClient(new Point(e.X, e.Y));
        var node = treeView1.GetNodeAt(point);
        if (node == null) { root = true; return true; }
        if (node.Tag is not RenderTreeNode renderNode) return false;

        var obj = renderNode.GetObject();
        if (obj is Group)
        {
            target = renderNode;
            return true;
        }

        if (obj.Parent == null || _currentRenderTree == null) { root = true; return true; }
        target = FindRenderNode(_currentRenderTree, obj.Parent.Guid);
        root = target == null;
        return true;
    }

    private bool CanDropNode(RenderTreeNode source, RenderTreeNode target, bool root)
    {
        var obj = source?.GetObject();
        if (obj == null) return false;
        if (root) return obj.Parent != null;
        if (target == null || target.GetObject() is not Group) return false;
        if (ReferenceEquals(obj, target.GetObject())) return false;
        if (obj.Parent != null && ReferenceEquals(obj.Parent, target.GetObject())) return false;
        for (var parent = target.GetObject().Parent; parent != null; parent = parent.Parent)
            if (ReferenceEquals(parent, obj)) return false;
        return true;
    }

    private RenderTreeNode DragSourceNode(DragEventArgs e) =>
        e.Data?.GetData(typeof(TreeNode)) is TreeNode node ? node.Tag as RenderTreeNode : null;

    private void TreeDragEnter(object sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(typeof(TreeNode)) == true ? DragDropEffects.Move : DragDropEffects.None;
    }

    private void TreeDragOver(object sender, DragEventArgs e)
    {
        var source = DragSourceNode(e);
        if (source == null || !TryGetDropTarget(e, out var target, out var root) || !CanDropNode(source, target, root))
        {
            e.Effect = DragDropEffects.None;
            return;
        }
        e.Effect = DragDropEffects.Move;
        var hover = treeView1.GetNodeAt(treeView1.PointToClient(new Point(e.X, e.Y)));
        hover?.Expand();
    }

    private void TreeDragDrop(object sender, DragEventArgs e)
    {
        var source = DragSourceNode(e);
        if (source == null || !TryGetDropTarget(e, out var target, out var root) || !CanDropNode(source, target, root))
            return;
        var targetObject = root ? null : target.GetObject();
        MoveObjectToGroup(source.GetObject(), targetObject, "拖放移动到组");
    }

    private static RenderTreeNode FindRenderNode(IEnumerable<RenderTreeNode> nodes, uint guid)
    {
        foreach (var node in nodes)
        {
            if (node.GetObject().Guid == guid) return node;
            if (node is RenderTreeGroup group)
            {
                var found = FindRenderNode(group, guid);
                if (found != null) return found;
            }
        }
        return null;
    }

    private void ShowAbout()
    {
        using var dialog = new AboutDialog(Icon);
        dialog.ShowDialog(this);
    }
}

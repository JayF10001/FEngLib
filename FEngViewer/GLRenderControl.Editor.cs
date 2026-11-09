using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using FEngLib.Objects;
using FEngLib.Packages;
using FEngRender.Data;

namespace FEngViewer;

public enum CanvasTool { Select, Move, Scale, Rotate, Pan }

public partial class GLRenderControl
{
    private float _zoom = 1;
    private PointF _pan;
    private bool _fit = true;
    private bool _panning, _dragging, _dragArmed;
    private Point _mouseStart;
    private PointF _panStart;
    private Vector3 _dragPosition;
    private Vector3 _dragPivot;
    private Quaternion _dragRotation;
    private Vector2 _startVector;
    private Matrix4x4 _dragInverse;
    private string _handle;
    private readonly Stopwatch _previewClock = new();
    private int _pausedTime;
    private IObject<ObjectData> _previewObject;
    private uint? _previewScript;
    private PackageAnimationPreview _packagePreview;
    private List<RenderTreeNode> _lastHits = new();
    private Point _lastHitPoint;
    private int _hitIndex;
    private CanvasTool _tool = CanvasTool.Move;
    public CanvasTool Tool
    {
        get => _tool;
        set { _tool=value; RequestFrame(); }
    }
    public RenderTreeNode HoveredNode { get; private set; }
    public bool ShowGrid { get; set; }
    public bool ShowOutlines { get; set; }
    public bool ShowNames { get; set; }
    public bool ShowGroups { get; set; }
    public bool IsDragging => _dragging;
    /// <summary>Workspace browsing allows navigation, but never object picking or model mutations.</summary>
    public bool ReadOnly { get; set; }
    public float Zoom => _fit ? Math.Max(0.001f, _previewViewport.Height / 480f) : _zoom;
    public PointF Pan => _pan;
    public bool Fit => _fit;
    public bool PreviewPlaying => _previewClock.IsRunning;
    public bool PackagePreviewActive => _packagePreview!=null;
    public int PackagePreviewObjectCount => _packagePreview?.ObjectCount??0;
    public bool IsPreviewOf(IObject<ObjectData> obj,uint id)=>ReferenceEquals(_previewObject,obj)&&_previewScript==id;
    public event Action<RenderTreeNode> ObjectPicked;
    public event Action<IObject<ObjectData>, bool> TransformFinished;
    public event Action TransformUpdated;
    public event Action<string> EditorStatus;
    public event Action PreviewStateChanged;

    public void SetView(float zoom, PointF pan, bool fit)
    { _fit = fit; _zoom = Math.Clamp(zoom, 0.1f, 8f); _pan = pan; PreviewViewportChanged?.Invoke(this, EventArgs.Empty); RequestFrame(); }
    public void FitToWindow() => SetView(1, PointF.Empty, true);
    public void SetZoom(float value, Point? focus = null)
    {
        var p = focus ?? new Point(_previewViewport.Left + _previewViewport.Width/2, _previewViewport.Top + _previewViewport.Height/2);
        var before = ScreenToLogical(p);
        _fit = false; _zoom = Math.Clamp(value,0.1f,8f);
        _pan = new PointF(p.X - (_previewViewport.Left + _previewViewport.Width/2f) - (before.X-320)*_zoom,
            p.Y - (_previewViewport.Top + _previewViewport.Height/2f) - (before.Y-240)*_zoom);
        PreviewViewportChanged?.Invoke(this,EventArgs.Empty);
        RequestFrame();
    }
    public PointF LogicalToScreen(PointF p) => new(
        _previewViewport.Left+_previewViewport.Width/2f+_pan.X+(p.X-320)*Zoom,
        _previewViewport.Top+_previewViewport.Height/2f+_pan.Y+(p.Y-240)*Zoom);
    public PointF FeToScreen(PointF p) => LogicalToScreen(new PointF(p.X+320,p.Y+240));
    public PointF ScreenToFe(Point p) { var v=ScreenToLogical(p); return new PointF(v.X-320,v.Y-240); }
    public void StartPreview(IObject<ObjectData> obj, uint id)
    { CancelInteraction(); _packagePreview=null; _previewObject=obj; _previewScript=id; _pausedTime=0; _previewClock.Restart(); PreviewStateChanged?.Invoke(); }
    public void StartPackagePreview(Package package)
    {
        CancelInteraction();ResetPreview();
        if(package==null)return;
        var preview=new PackageAnimationPreview(package);
        SelectedNode=null;
        if(preview.ObjectCount==0)
        {EditorStatus?.Invoke("当前 FNG 没有可自动播放的展示动画，保持初始布局。");return;}
        _packagePreview=preview;_previewClock.Restart();
        EditorStatus?.Invoke($"全体动画预览：{preview.ObjectCount} 个对象，30 秒后回到初始布局。");
        PreviewStateChanged?.Invoke();RequestFrame();
    }
    public void PausePreview()
    { if (_previewClock.IsRunning) { _pausedTime+=(int)_previewClock.ElapsedMilliseconds; _previewClock.Reset(); PreviewStateChanged?.Invoke(); } }
    public void ResumePreview()
    { if ((_previewScript.HasValue || _packagePreview!=null) && !_previewClock.IsRunning) {_previewClock.Start();PreviewStateChanged?.Invoke();} }
    public void ResetPreview()
    {
        var hadPreview=_packagePreview!=null || _previewScript.HasValue;
        _previewClock.Reset(); _pausedTime=0; _previewObject=null; _previewScript=null; _packagePreview=null;
        if(_renderer!=null){_renderer.PreviewFrames=null;_renderer.PreviewObject=null;_renderer.PreviewScript=null;}
        PrepareLayout();
        if(hadPreview)PreviewStateChanged?.Invoke();
    }
    internal PreviewPlaybackState CapturePreviewState() => _previewScript.HasValue || _packagePreview != null
        ? new PreviewPlaybackState
        {
            ObjectGuid = _previewObject?.Guid, ScriptId = _previewScript, WholePackage = _packagePreview != null,
            Elapsed = _pausedTime + (int)_previewClock.ElapsedMilliseconds, Playing = PreviewPlaying
        } : null;

    internal void RestorePreviewState(Package package, PreviewPlaybackState state)
    {
        if (state == null) return;
        if (state.WholePackage) StartPackagePreview(package);
        else
        {
            var obj = package.Objects.FirstOrDefault(o => o.Guid == state.ObjectGuid);
            if (obj == null || !state.ScriptId.HasValue || !obj.GetScripts().Any(s => s.Id == state.ScriptId)) return;
            StartPreview(obj, state.ScriptId.Value);
        }
        _pausedTime = state.Elapsed;
        _previewClock.Reset();
        if (state.Playing) _previewClock.Start();
        PreviewStateChanged?.Invoke();
        RequestFrame();
    }
    public void PrepareLayout() { RenderGeometry.Prepare(_renderTree); RequestFrame(); }

    private void InitializeEditorInput()
    {
        openglControl1.MouseDown += EditorMouseDown;
        openglControl1.MouseMove += EditorMouseMove;
        openglControl1.MouseUp += EditorMouseUp;
        openglControl1.MouseLeave += (_,_) => { HoveredNode=null; RequestFrame(); };
        openglControl1.MouseWheel += (_,e) => { if (!_dragging) SetZoom(Zoom * (e.Delta>0?1.15f:1/1.15f),e.Location); };
        openglControl1.MouseCaptureChanged += (_,_) => { if (!openglControl1.Capture) CancelInteraction(); };
        openglControl1.KeyDown += EditorKeyDown;
        openglControl1.TabStop=true;
    }
    public void CancelInteraction()
    {
        if (_dragging)
        {
            _dragging=false;
            TransformFinished?.Invoke(SelectedNode?.GetObject(),false);
        }
        _panning=false;
        _dragArmed=false;
        openglControl1.Capture=false;
        PrepareLayout();
    }
    private bool CanTransform(RenderTreeNode node, string handle)
    {
        if (node == null) return false;
        if(node.GetObject().Type is not (ObjectType.Image or ObjectType.ColoredImage or ObjectType.MultiImage or ObjectType.SimpleImage or ObjectType.String or ObjectType.Group))
        {EditorStatus?.Invoke("此类型暂不支持画布变换，请使用高级属性。");return false;}
        if (node.GetObject().Flags.HasFlag(ObjectFlags.ObjectLocked)) { EditorStatus?.Invoke("对象已锁定"); return false; }
        if (node.PoseError != null) { EditorStatus?.Invoke("轨道无法求值："+node.PoseError); return false; }
        if (!RenderGeometry.TryParentPlaneInverse(node,out _))
        { EditorStatus?.Invoke("父变换或尺寸不可逆，无法在画布变换"); return false; }
        if(handle.StartsWith("scale") && (Math.Abs(node.EvaluatedData.Size.X)<0.000001f || Math.Abs(node.EvaluatedData.Size.Y)<0.000001f))
        {EditorStatus?.Invoke("零尺寸轴无法按比例缩放，请检查高级属性。");return false;}
        var rot=node.EvaluatedData.Rotation;
        var parent=node.ParentTransform;
        if((handle.StartsWith("scale") || handle=="rotate") && (Math.Abs(rot.X)>0.0001f || Math.Abs(rot.Y)>0.0001f ||
            Math.Abs(parent.M13)>0.0001f || Math.Abs(parent.M23)>0.0001f || Math.Abs(parent.M31)>0.0001f || Math.Abs(parent.M32)>0.0001f))
        {EditorStatus?.Invoke("三维倾斜姿态请使用高级属性；画布手柄仅编辑二维旋转与缩放。");return false;}
        return true;
    }
    private PointF PivotPoint(RenderTreeNode node)
    {
        var d=node.EvaluatedData;
        var p=Vector3.Transform(d.Position+d.Pivot,node.ParentTransform);
        return new PointF(p.X,p.Y);
    }
    private PointF[] HandlePoints(RenderTreeNode node)
    {
        var p=RenderGeometry.Polygon(node).Select(FeToScreen).ToArray();
        if(p.Length!=4)return Array.Empty<PointF>();
        return p.Concat(Enumerable.Range(0,4).Select(i=>new PointF((p[i].X+p[(i+1)%4].X)/2,(p[i].Y+p[(i+1)%4].Y)/2))).ToArray();
    }
    private string HitHandle(Point p)
    {
        if (SelectedNode==null || Tool==CanvasTool.Pan) return null;
        var handles=HandlePoints(SelectedNode);
        if (Distance(RotationHandlePoint(SelectedNode),p)<9) return "rotate";
        var pivot=FeToScreen(PivotPoint(SelectedNode));
        if (Tool==CanvasTool.Rotate && Math.Abs(Distance(pivot,p)-40)<8) return "rotate";
        for(var i=0;i<handles.Length;i++) if (Distance(handles[i],p)<8) return "scale"+i;
        if (Tool is CanvasTool.Move or CanvasTool.Select)
        {
            if (Distance(pivot,p)<7) return "move";
            if (Math.Abs(p.Y-pivot.Y)<7 && p.X>=pivot.X+10 && p.X<=pivot.X+46) return "x";
            if (Math.Abs(p.X-pivot.X)<7 && p.Y>=pivot.Y+10 && p.Y<=pivot.Y+46) return "y";
        }
        return null;
    }
    private PointF RotationHandlePoint(RenderTreeNode node)
    {
        var p=HandlePoints(node);
        if(p.Length<4) { var pivot=FeToScreen(PivotPoint(node)); return new PointF(pivot.X,pivot.Y-60); }
        var center=new PointF(p.Take(4).Average(v=>v.X),p.Take(4).Average(v=>v.Y));
        var top=new PointF((p[0].X+p[1].X)/2,(p[0].Y+p[1].Y)/2);
        var length=Distance(center,top);
        return length<0.001f?new PointF(top.X,top.Y-26):
            new PointF(top.X+(top.X-center.X)*26/length,top.Y+(top.Y-center.Y)*26/length);
    }
    private List<RenderTreeNode> HitObjects(Point location)
    {
        if(_renderTree==null || !PreviewViewport.Contains(location)) return new();
        var fe=ScreenToFe(location);
        // Reverse the exact painter order, including equal-Z ordering. Children take priority
        // over group bounds. Hover and click share this result so the highlighted target wins.
        return RenderTree.GetAllTreeNodesForRendering(_renderTree).OrderByDescending(n=>n.GetZ())
            .Reverse().Where(n=>n.BlendedColor.Alpha>0 && RenderGeometry.Contains(n,fe))
            .OrderBy(n=>n is RenderTreeGroup?1:0).ToList();
    }
    private static float Distance(PointF a,PointF b) => MathF.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
    private void EditorMouseDown(object sender,MouseEventArgs e)
    {
        openglControl1.Focus();
        if (e.Button==MouseButtons.Middle || e.Button==MouseButtons.Left && (ReadOnly || Tool==CanvasTool.Pan))
        { _panning=true; _mouseStart=e.Location; _panStart=_pan; HoveredNode=null; openglControl1.Cursor=Cursors.Hand; openglControl1.Capture=true; return; }
        if(e.Button!=MouseButtons.Left || _renderTree==null || !PreviewViewport.Contains(e.Location)) return;
        HoveredNode=null;
        if(PackagePreviewActive)
        {
            // Select the layer at its displayed animated location before returning to INIT.
            // The first click enters editing; a following drag starts from the stable layout.
            var animatedHit=HitObjects(e.Location).FirstOrDefault();
            ResetPreview();SelectedNode=animatedHit;ObjectPicked?.Invoke(animatedHit);return;
        }
        ResetPreview();
        var handle=HitHandle(e.Location);
        var fe=ScreenToFe(e.Location);
        if (handle==null)
        {
            var hits=HitObjects(e.Location);
            if((ModifierKeys & Keys.Alt)!=0 && Distance(_lastHitPoint,e.Location)<5 &&
                hits.Select(n=>n.GetObject()).SequenceEqual(_lastHits.Select(n=>n.GetObject()))) _hitIndex++;
            else _hitIndex=0;
            _lastHitPoint=e.Location; _lastHits=hits;
            var hit=hits.Count==0?null:hits[_hitIndex%hits.Count];
            SelectedNode=hit;
            ObjectPicked?.Invoke(hit);
            if ((ModifierKeys & Keys.Alt)!=0 || hit==null) return;
            handle="move";
        }
        if(handle==null || !CanTransform(SelectedNode,handle)) return;
        var node=SelectedNode;
        _dragPosition=node.EvaluatedData.Position; _dragRotation=node.EvaluatedData.Rotation;
        _dragPivot=node.EvaluatedData.Pivot;
        _handle=handle; _mouseStart=e.Location;
        RenderGeometry.TryParentPlaneInverse(node,out _dragInverse);
        var local=Vector3.Transform(new Vector3(fe.X,fe.Y,0),_dragInverse);
        _startVector=new Vector2(local.X-_dragPosition.X-node.EvaluatedData.Pivot.X,
            local.Y-_dragPosition.Y-node.EvaluatedData.Pivot.Y);
        _dragArmed=true; openglControl1.Capture=true;
    }
    private void EditorMouseMove(object sender,MouseEventArgs e)
    {
        if(_panning)
        { _pan=new PointF(_panStart.X+e.X-_mouseStart.X,_panStart.Y+e.Y-_mouseStart.Y); PreviewViewportChanged?.Invoke(this,EventArgs.Empty); RequestFrame(); return; }
        if (ReadOnly) { HoveredNode=null; openglControl1.Cursor=Cursors.Hand; return; }
        if(_dragArmed && !_dragging && e.Button==MouseButtons.Left)
        {
            var threshold=SystemInformation.DragSize;
            _dragging=Math.Abs(e.X-_mouseStart.X)>=Math.Max(3,threshold.Width/2) ||
                Math.Abs(e.Y-_mouseStart.Y)>=Math.Max(3,threshold.Height/2);
        }
        if(!_dragging || SelectedNode==null)
        {
            if(_dragArmed) return;
            var h=HitHandle(e.Location);
            var hover=Tool==CanvasTool.Pan?null:h!=null?SelectedNode:HitObjects(e.Location).FirstOrDefault();
            if(!ReferenceEquals(hover,HoveredNode)) { HoveredNode=hover; RequestFrame(); }
            openglControl1.Cursor=Tool==CanvasTool.Pan?Cursors.Hand:h=="rotate"?Cursors.Cross:
                h!=null?Cursors.SizeAll:hover!=null?Cursors.Hand:Cursors.Default;
            return;
        }
        // Recreate the pending frame from the transaction's original model, never accumulate rounding drift.
        TransformFinished?.Invoke(SelectedNode.GetObject(),false);
        var obj=SelectedNode.GetObject();
        try
        {
            var fe=ScreenToFe(e.Location);
            var local=Vector3.Transform(new Vector3(fe.X,fe.Y,0),_dragInverse);
            var pivot=_dragPivot;
            var v=new Vector2(local.X-_dragPosition.X-pivot.X,local.Y-_dragPosition.Y-pivot.Y);
            if(_handle is "move" or "x" or "y")
            {
                var start=ScreenToFe(_mouseStart);
                var delta=Vector3.TransformNormal(new Vector3(fe.X-start.X,fe.Y-start.Y,0),_dragInverse);
                if(_handle=="x") delta.Y=0; if(_handle=="y") delta.X=0;
                delta.Z=0; ObjectPropertyEditing.SetPosition(obj,obj.Data.Position+delta);
            }
            else if(_handle=="rotate")
            {
                var a=MathF.Atan2(v.Y,v.X)-MathF.Atan2(_startVector.Y,_startVector.X);
                if((ModifierKeys & Keys.Shift)!=0) a=MathF.Round(a/(MathF.PI/12))*(MathF.PI/12);
                ObjectPropertyEditing.Rotate(obj,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,a));
            }
            else
            {
                var inverseRot=Quaternion.Inverse(_dragRotation);
                var a=Vector3.Transform(new Vector3(_startVector,0),inverseRot);
                var b=Vector3.Transform(new Vector3(v,0),inverseRot);
                var index=int.Parse(_handle[5..]);
                float x=Math.Abs(a.X)<0.0001f?1:b.X/a.X, y=Math.Abs(a.Y)<0.0001f?1:b.Y/a.Y;
                if(index<4) { var f=Math.Abs(x-1)>Math.Abs(y-1)?x:y; x=y=f; }
                else if(index is 4 or 6) x=1; else y=1;
                ObjectPropertyEditing.Scale(obj,new Vector3(Math.Clamp(x,0.001f,1000),Math.Clamp(y,0.001f,1000),1));
            }
            PrepareLayout(); TransformUpdated?.Invoke();
        }
        catch(Exception ex) when(ex is InvalidOperationException or ArgumentException)
        { EditorStatus?.Invoke(ex.Message); }
    }
    private void EditorMouseUp(object sender,MouseEventArgs e)
    {
        if(_dragging)
        {
            var obj=SelectedNode?.GetObject(); _dragging=false;
            TransformFinished?.Invoke(obj,true);
        }
        _dragArmed=false; _panning=false; openglControl1.Capture=false;
        RequestFrame();
    }
    private void EditorKeyDown(object sender,KeyEventArgs e)
    {
        if(e.KeyCode==Keys.Escape) { CancelInteraction(); e.Handled=true; }
    }
    private void DrawEditorOverlay(Graphics graphics)
    {
        if(graphics==null) return;
        // GDIDraw runs after SharpGL presents the DIB. The work area and canvas are already
        // painted by GL, so this overlay only adds selection chrome and the canvas border.
        var state=graphics.Save();
        try
        {
            if(_renderTree!=null)
            {
            graphics.SetClip(PreviewViewport);
            graphics.SmoothingMode=SmoothingMode.AntiAlias;
            PointF[] ScreenPolygon(RenderTreeNode node)=>RenderGeometry.Polygon(node).Select(FeToScreen).ToArray();
            bool Valid(PointF[] points)=>points.Length>=3 && points.All(p=>float.IsFinite(p.X)&&float.IsFinite(p.Y));
            void Polygon(PointF[] points,Color color,float width=1)
            {
                if(!Valid(points)) return;
                using var pen=new Pen(color,width);graphics.DrawPolygon(pen,points);
            }
            void Line(PointF a,PointF b,Color color,float width=1)
            { using var pen=new Pen(color,width);graphics.DrawLine(pen,a,b); }
            void Circle(PointF p,float radius,Color color,bool fill=false)
            {
                var rect=new RectangleF(p.X-radius,p.Y-radius,2*radius,2*radius);
                if(fill){using var brush=new SolidBrush(color);graphics.FillEllipse(brush,rect);}
                else {using var pen=new Pen(color,2);graphics.DrawEllipse(pen,rect);}
            }
            if(ShowGrid)
            {
                var left=ScreenToFe(PreviewViewport.Location);var right=ScreenToFe(new Point(PreviewViewport.Right,PreviewViewport.Bottom));
                var step=Zoom<0.5f?64:16;
                for(var x=MathF.Floor(left.X/step)*step;x<=right.X;x+=step)
                    Line(FeToScreen(new PointF(x,left.Y)),FeToScreen(new PointF(x,right.Y)),Color.FromArgb(55,60,66));
                for(var y=MathF.Floor(left.Y/step)*step;y<=right.Y;y+=step)
                    Line(FeToScreen(new PointF(left.X,y)),FeToScreen(new PointF(right.X,y)),Color.FromArgb(55,60,66));
            }
            var nodes=RenderTree.GetAllTreeNodesForRendering(_renderTree).ToArray();
            if(ShowOutlines || ShowGroups)
                foreach(var n in nodes.Where(n=>ShowOutlines || n is RenderTreeGroup)) Polygon(ScreenPolygon(n),Color.SlateGray);
            if(HoveredNode!=null && !ReferenceEquals(HoveredNode,SelectedNode))
            {
                var p=ScreenPolygon(HoveredNode);
                if(Valid(p))
                {
                    using var tint=new SolidBrush(Color.FromArgb(35,255,207,64));graphics.FillPolygon(tint,p);
                    Polygon(p,Color.FromArgb(200,20,20,20),4);Polygon(p,Color.Gold,2);
                }
            }
            if(SelectedNode!=null)
            {
                var p=ScreenPolygon(SelectedNode);
                if(Valid(p))
                {
                    using var tint=new SolidBrush(Color.FromArgb(20,0,191,255));graphics.FillPolygon(tint,p);
                    Polygon(p,Color.FromArgb(220,10,18,24),5);Polygon(p,Color.DeepSkyBlue,2);
                }
                if(Tool!=CanvasTool.Pan)
                {
                    foreach(var h in HandlePoints(SelectedNode))
                    {
                        var box=new RectangleF(h.X-4,h.Y-4,8,8);
                        using var fill=new SolidBrush(Color.White);using var border=new Pen(Color.DeepSkyBlue,2);
                        graphics.FillRectangle(fill,box);graphics.DrawRectangle(border,box.X,box.Y,box.Width,box.Height);
                    }
                    var pivot=FeToScreen(PivotPoint(SelectedNode));
                    if(Tool is CanvasTool.Move or CanvasTool.Select)
                    {
                        void Axis(PointF end,Color color)
                        {
                            using var cap=new AdjustableArrowCap(4,5);
                            using var pen=new Pen(color,2.5f){CustomEndCap=cap};
                            graphics.DrawLine(pen,pivot,end);
                        }
                        Axis(new PointF(pivot.X+46,pivot.Y),Color.OrangeRed);
                        Axis(new PointF(pivot.X,pivot.Y+46),Color.LimeGreen);
                        Circle(pivot,4,Color.White,true);
                    }
                    var rotate=RotationHandlePoint(SelectedNode);
                    var top=p.Length>=4?new PointF((p[0].X+p[1].X)/2,(p[0].Y+p[1].Y)/2):pivot;
                    Line(top,rotate,Color.Gold,2);Circle(rotate,6,Color.FromArgb(24,26,30),true);Circle(rotate,6,Color.Gold);
                    if(Tool==CanvasTool.Rotate) Circle(pivot,40,Color.Gold);
                }
            }
            if(ShowNames)
                foreach(var n in nodes)
                {
                    var bounds=RenderGeometry.Bounds(n);var p=FeToScreen(new PointF(bounds.Left,bounds.Top));
                    if(PreviewViewport.Contains(Point.Round(p)))
                    {
                        using var brush=new SolidBrush(Color.LightSkyBlue);
                        graphics.DrawString(n.GetObject().Name??$"{n.GetObject().NameHash:X8}",Font,brush,p);
                    }
                }
            }
        }
        finally { graphics.Restore(state); }
        DrawCanvasBorder(graphics);
    }

    /// <summary>One device-independent pixel; maps to one physical pixel at 100% DPI.</summary>
    private int CanvasBorderWidth => Math.Max(1, (int)Math.Round(DeviceDpi / 96f));

    /// <summary>
    /// Screen rectangle occupied by the actual FNG canvas at the current zoom and pan. The GL
    /// viewport is only the clipping window; at anything other than fit it is larger than the
    /// scaled canvas, so chrome must follow this rectangle instead of the viewport.
    /// </summary>
    private Rectangle CanvasBounds
    {
        get
        {
            var viewport = PreviewViewport;
            if (_fit && _pan.X == 0 && _pan.Y == 0) return viewport;

            var canvas = PreviewLayout.CanvasSize(_previewAspect);
            var scale = Zoom;
            var width = Math.Max(1, (int)Math.Round(canvas.Width * scale));
            var height = Math.Max(1, (int)Math.Round(canvas.Height * scale));
            var centerX = viewport.Left + viewport.Width / 2f + _pan.X;
            var centerY = viewport.Top + viewport.Height / 2f + _pan.Y;
            return new Rectangle(
                (int)Math.Round(centerX - width / 2f),
                (int)Math.Round(centerY - height / 2f),
                width, height);
        }
    }

    /// <summary>Part of the canvas that is actually visible inside the fixed-ratio GL viewport.</summary>
    private Rectangle CanvasClip => Rectangle.Intersect(CanvasBounds, PreviewViewport);

    private void DrawCanvasBorder(Graphics graphics)
    {
        var viewport = CanvasClip;
        if (viewport.Width < 2 || viewport.Height < 2) return;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        using var pen = new Pen(CanvasBackdrop.Border, CanvasBorderWidth)
        {
            Alignment = PenAlignment.Inset
        };
        graphics.DrawRectangle(pen, viewport.X, viewport.Y, viewport.Width - 1, viewport.Height - 1);
    }

    private void UpdatePreviewFrame()
    {
        if(_renderer==null) return;
        var elapsed=_pausedTime+(int)_previewClock.ElapsedMilliseconds;
        if(_packagePreview!=null)
        {
            if(elapsed>=PackageAnimationPreview.DurationMilliseconds)
            {ResetPreview();EditorStatus?.Invoke("全体动画预览结束，已回到初始布局。可点击“全体预览”再次播放。");return;}
            _renderer.PreviewObject=null;_renderer.PreviewScript=null;
            _renderer.PreviewFrames=_packagePreview.Sample(elapsed);
            return;
        }
        _renderer.PreviewFrames=null;
        _renderer.PreviewObject=_previewObject; _renderer.PreviewScript=_previewScript;
        var t=elapsed;
        var script=_previewObject?.GetScripts().FirstOrDefault(s=>s.Id==_previewScript);
        var visited=new Dictionary<uint,int>();
        for(var hops=0;script!=null && hops<256;hops++)
        {
            var length=(int)Math.Min(script.Length,int.MaxValue);
            if(visited.TryGetValue(script.Id,out var earlier))
            {
                var cycle=earlier-t;
                t=cycle>0?t%cycle:0;
                visited.Clear();
            }
            visited[script.Id]=t;
            if((script.Flags&1)!=0){t=length>0?t%length:0;break;}
            if(t<=length)break;
            var next=script.ChainedId.HasValue?_previewObject.GetScripts().FirstOrDefault(s=>s.Id==script.ChainedId):null;
            if(next==null){t=length;break;}
            t-=length;script=next;
        }
        _renderer.PreviewScript=script?.Id;
        _renderer.PreviewTime=t;
    }
}

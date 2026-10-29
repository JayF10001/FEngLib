using CommandLine;
using FEngLib;
using FEngLib.Messaging;
using FEngLib.Objects;
using FEngLib.Packages;
using FEngLib.Scripts;
using FEngLib.Structures;
using FEngRender.Data;
using FEngViewer.Properties;
using FEngViewer.Wrappers;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Image = FEngLib.Objects.Image;

namespace FEngViewer;

public partial class PackageView : Form
{
	internal Package _currentPackage;
	internal RenderTree _currentRenderTree;
	private PackageViewExtensions _packageViewExtensions;

	private TreeNode _rootNode;

	public PackageView()
	{
		InitializeComponent();
		_packageViewExtensions = new PackageViewExtensions(this);
		InitializeWorkspace();
		SetFormDetails();

		var imageList = new ImageList();
		imageList.Images.Add("TreeItem_Package", Resources.TreeItem_Package);
		imageList.Images.Add("TreeItem_String", Resources.TreeItem_String);
		imageList.Images.Add("TreeItem_Group", Resources.TreeItem_Group);
		imageList.Images.Add("TreeItem_Image", Resources.TreeItem_Image);
		imageList.Images.Add("TreeItem_Script", Resources.TreeItem_Script);
		imageList.Images.Add("TreeItem_ScriptTrack", Resources.TreeItem_ScriptTrack);
		imageList.Images.Add("TreeItem_ScriptEvent", Resources.TreeItem_ScriptEvent);
		imageList.Images.Add("TreeItem_Movie", Resources.TreeItem_Movie);
		imageList.Images.Add("TreeItem_ColoredImage", Resources.TreeItem_ColoredImage);
		imageList.Images.Add("TreeItem_MultiImage", Resources.TreeItem_MultiImage);
		imageList.Images.Add("TreeItem_ObjectList", Resources.TreeItem_ObjectList);
		imageList.Images.Add("TreeItem_ResourceList", Resources.TreeItem_ResourceList);
		imageList.Images.Add("TreeItem_GenericResource", Resources.TreeItem_GenericResource);
		imageList.Images.Add("TreeItem_Font", Resources.TreeItem_Font);
		imageList.Images.Add("TreeItem_Keyframe", Resources.TreeItem_Keyframe);
		imageList.Images.Add("TreeItem_SimpleImage", Resources.TreeItem_SimpleImage);
		imageList.Images.Add("TreeItem_Model", Resources.TreeItem_Model);
		imageList.Images.Add("TreeItem_Effect", Resources.TreeItem_Effect);
		imageList.Images.Add("TreeItem_AnimatedImage", Resources.TreeItem_AnimatedImage);
		imageList.Images.Add("TreeItem_List", Resources.TreeItem_List);
		imageList.Images.Add("TreeItem_CodeList", Resources.TreeItem_CodeList);
		imageList.Images.Add("TreeItem_Message", Resources.TreeItem_Message);
		imageList.Images.Add("TreeItem_GenericObject", Resources.TreeItem_GenericObject);
		treeView1.ImageList = imageList;
		InitializeEditorWorkbench();
	}

	private void PackageView_Load(object sender, EventArgs e)
	{
		var args = Environment.GetCommandLineArgs().Skip(1).ToArray();

		var opts = Parser.Default.ParseArguments<Options>(args);

		opts.WithParsed(parsed => OpenStartupFile(parsed.InputFile))
			.WithNotParsed(err => Application.Exit());
	}

	private void OpenStartupFile(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return;

		if (!File.Exists(path))
		{
			MessageBox.Show(this, $"找不到文件：\n{path}", "FEngViewer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}

		var isJson = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
		ImportAndOpen(new[] { path }, isJson, activateAll: false);
	}

	[UsedImplicitly]
	private class Options
	{
		[Option('i', "input")] public string InputFile { get; [UsedImplicitly] set; }
	}

	private void Render()
	{
		viewOutput.Render(_currentRenderTree);
	}


	private void PopulateTreeView(Package package, IEnumerable<RenderTreeNode> feObjectNodes)
	{
		// map group guid to children guids
		var restoreFlag = _restoringTree;
		_restoringTree = true;
		treeView1.BeginUpdate();
		try
		{
		treeView1.Nodes.Clear();

		_rootNode = treeView1.Nodes.Add(package.Name);
		_rootNode.Name = "package";

		if (!_simpleTree)
		{
		var resourceListNode = _rootNode.Nodes.Add("资源请求");
		resourceListNode.Name = "resources";
		resourceListNode.ImageKey = resourceListNode.SelectedImageKey = "TreeItem_ResourceList";

		foreach (var resourceRequest in package.ResourceRequests)
		{
			var resourceRequestNode = resourceListNode.Nodes.Add(resourceRequest.Name ?? $"0x{resourceRequest.ID:X8}");
			resourceRequestNode.Name = $"resource:{resourceRequest.ID:X8}";
			resourceRequestNode.Tag = resourceRequest;

			resourceRequestNode.ImageKey = resourceRequestNode.SelectedImageKey = resourceRequest.Type switch
			{
				ResourceType.Image => "TreeItem_Image",
				ResourceType.MultiImage => "TreeItem_MultiImage",
				ResourceType.Movie => "TreeItem_Movie",
				ResourceType.Font => "TreeItem_Font",
				ResourceType.Model => "TreeItem_Model",
				ResourceType.AnimatedImage => "TreeItem_AnimatedImage",
				ResourceType.Effect => "TreeItem_Effect",
				_ => "TreeItem_GenericResource"
			};
		}

		var messageResponseListNode = _rootNode.Nodes.Add("消息响应");
		messageResponseListNode.Name = "messages";
		messageResponseListNode.ImageKey = messageResponseListNode.SelectedImageKey = "TreeItem_Message";
		foreach (var messageResponses in package.MessageResponses)
		{
			var name = AppService.Instance.HashResolver.ResolveNameHash(null, messageResponses.Id);
			var messageResponseNode = messageResponseListNode.Nodes.Add(name ?? messageResponses.Id.ToString());
			messageResponseNode.Name = $"messages/response:{messageResponseNode.Index}";
			messageResponseNode.Tag = messageResponses;
		}

		var messageDefinitionsListNode = _rootNode.Nodes.Add("消息定义");
		messageDefinitionsListNode.Name = "definitions";
		messageDefinitionsListNode.ImageKey = messageDefinitionsListNode.SelectedImageKey = "TreeItem_Message";
		foreach (var messageDefinitions in package.MessageDefinitions)
		{
			var messageDefinitionsNode = messageDefinitionsListNode.Nodes.Add(messageDefinitions.Name);
			messageDefinitionsNode.Name = $"definitions/item:{messageDefinitionsNode.Index}";
			messageDefinitionsNode.Tag = messageDefinitions;
		}

		var messageTargetsNodeList = _rootNode.Nodes.Add("消息目标");
		messageTargetsNodeList.Name = "targets";
		messageTargetsNodeList.ImageKey = messageTargetsNodeList.SelectedImageKey = "TreeItem_Message";
		foreach (var messageTargetsLists in package.MessageTargetLists)
		{
			var name = AppService.Instance.HashResolver.ResolveNameHash(null, messageTargetsLists.MsgId);
			var messageTargetsNode = messageTargetsNodeList.Nodes.Add(name ?? messageTargetsLists.MsgId.ToString());
			messageTargetsNode.Name = $"targets/item:{messageTargetsNode.Index}";
			messageTargetsNode.Tag = messageTargetsLists;
		}
		}

		//var objectListNode = rootNode.Nodes.Add("Objects");
		//objectListNode.ImageKey = "TreeItem_ObjectList";
		ApplyObjectsToTreeNodes(feObjectNodes, _rootNode.Nodes);

		_rootNode.Expand();
		// treeView1.ExpandAll();

		if (_expandTreeButton != null) _expandTreeButton.Text = "⊟";
		treeView1.SelectedNode = _rootNode;
		}
		finally { treeView1.EndUpdate(); _restoringTree = restoreFlag; }
	}

	private void ApplyObjectsToTreeNodes(IEnumerable<RenderTreeNode> objectNodes,
		TreeNodeCollection treeNodes)
	{
		foreach (var feObjectNode in objectNodes)
		{
			var objTreeNode = CreateObjectTreeNode(treeNodes, feObjectNode);
			if (feObjectNode is RenderTreeGroup grp)
				ApplyObjectsToTreeNodes(grp, objTreeNode.Nodes);
		}
	}

	private TreeNode CreateObjectTreeNode(TreeNodeCollection collection, RenderTreeNode viewNode)
	{
		var feObj = viewNode.GetObject();
		var nodeImageKey = feObj.Type switch
		{
			ObjectType.String => "TreeItem_String",
			ObjectType.Image => "TreeItem_Image",
			ObjectType.Group => "TreeItem_Group",
			ObjectType.Movie => "TreeItem_Movie",
			ObjectType.ColoredImage => "TreeItem_ColoredImage",
			ObjectType.MultiImage => "TreeItem_MultiImage",
			ObjectType.AnimImage => "TreeItem_AnimatedImage",
			ObjectType.Effect => "TreeItem_Effect",
			ObjectType.Model => "TreeItem_Model",
			ObjectType.SimpleImage => "TreeItem_SimpleImage",
			ObjectType.List => "TreeItem_List",
			ObjectType.CodeList => "TreeItem_CodeList",
			_ => "TreeItem_GenericObject"
		};

		var nodeText = PackageViewExtensions.GetObjectText(feObj) + (feObj.Flags.HasFlag(ObjectFlags.Invisible) ? "（隐藏）" : "");
		var objTreeNode = collection.Add(nodeText);
		objTreeNode.Tag = viewNode;
		objTreeNode.Name = PackageViewExtensions.GetObjectTreeKey(feObj);
		if (nodeImageKey != null)
			objTreeNode.ImageKey = objTreeNode.SelectedImageKey = nodeImageKey;

		if (!_simpleTree)
		{
		foreach (var script in feObj.GetScripts())
			CreateScriptTreeNode(objTreeNode.Nodes, script);
		foreach (var messageResponses in feObj.MessageResponses)
			CreateMessageResponses(objTreeNode.Nodes, messageResponses);
		}

		return objTreeNode;
	}

	private static void CreateMessageResponses(TreeNodeCollection collection, MessageResponse messageResponse)
	{
		var name = AppService.Instance.HashResolver.ResolveNameHash(null, messageResponse.Id);
		var node = collection.Add(name ?? messageResponse.Id.ToString());
		node.Name = $"{node.Parent?.Name}/response:{node.Index}";
		// ReSharper disable once LocalizableElement
		node.ImageKey = node.SelectedImageKey = "TreeItem_Message";
		node.Tag = messageResponse;

		foreach (var message in messageResponse.Responses)
		{
			string nameResponse = null;
			if (message.IntParam.HasValue)
			{
				nameResponse = AppService.Instance.HashResolver.ResolveNameHash(null, message.IntParam.Value);
			}
			var eventNode = node.Nodes.Add(nameResponse ?? (message.IntParam.HasValue ? "IntParam " + message.IntParam.ToString() : "Id " + message.Id.ToString()));
			eventNode.Name = $"{node.Name}/response:{eventNode.Index}";
			eventNode.ImageKey = eventNode.SelectedImageKey = "TreeItem_Message";
			eventNode.Tag = message;
		}
	}

	private static void CreateScriptTreeNode(TreeNodeCollection collection, Script script)
	{
		var node = collection.Add(script.Name ?? $"0x{script.Id:X}");
		node.Name = $"{node.Parent?.Name}/script:{script.Id:X8}";
		// ReSharper disable once LocalizableElement
		node.ImageKey = node.SelectedImageKey = "TreeItem_Script";
		node.Tag = script;

		foreach (var scriptEvent in script.Events)
		{
			var eventNode =
				node.Nodes.Add($"0x{scriptEvent.EventId:X} -> {scriptEvent.Target:X} @ T={scriptEvent.Time}");
			eventNode.Name = $"{node.Name}/event:{eventNode.Index}";
			eventNode.ImageKey = eventNode.SelectedImageKey = "TreeItem_ScriptEvent";
			eventNode.Tag = scriptEvent;
		}

		var scriptTracks = script.GetTracks();

		CreateTrackNode(node, scriptTracks.Color, "Color");
		CreateTrackNode(node, scriptTracks.Pivot, "Pivot");
		CreateTrackNode(node, scriptTracks.Position, "Position");
		CreateTrackNode(node, scriptTracks.Rotation, "Rotation");
		CreateTrackNode(node, scriptTracks.Size, "Size");

		if (scriptTracks is ImageScriptTracks imageScriptTracks)
		{
			CreateTrackNode(node, imageScriptTracks.UpperLeft, "UpperLeft");
			CreateTrackNode(node, imageScriptTracks.LowerRight, "LowerRight");

			if (imageScriptTracks is MultiImageScriptTracks multiImageScriptTracks)
			{
				CreateTrackNode(node, multiImageScriptTracks.TopLeft1, "TopLeft1");
				CreateTrackNode(node, multiImageScriptTracks.TopLeft2, "TopLeft2");
				CreateTrackNode(node, multiImageScriptTracks.TopLeft3, "TopLeft3");
				CreateTrackNode(node, multiImageScriptTracks.BottomRight1, "BottomRight1");
				CreateTrackNode(node, multiImageScriptTracks.BottomRight2, "BottomRight2");
				CreateTrackNode(node, multiImageScriptTracks.BottomRight3, "BottomRight3");
				CreateTrackNode(node, multiImageScriptTracks.PivotRotation, "PivotRotation");
			}
		}
	}

	private static void CreateTrackNode(TreeNode scriptNode, Track track, string name)
	{
		if (track == null)
			return;

		var trackNode = scriptNode.Nodes.Add(EditorStrings.Translate(name));
		trackNode.Name = $"{scriptNode.Name}/track:{name}";
		trackNode.ImageKey = trackNode.SelectedImageKey = "TreeItem_ScriptTrack";
		trackNode.Tag = track;

		void AddNodeKey<T>(int time, object value, T tag) where T : TrackNode
		{
			var node = trackNode.Nodes.Add($"T={time}: {value}");
			node.Name = $"{trackNode.Name}/key:{trackNode.Nodes.Count-1}";
			node.ImageKey = node.SelectedImageKey = "TreeItem_Keyframe";
			node.Tag = tag;
		}

		switch (track)
		{
			case Vector2Track vector2Track:
				foreach (var deltaKey in vector2Track.DeltaKeys)
					AddNodeKey(deltaKey.Time, deltaKey.Val, deltaKey);
				break;
			case Vector3Track vector3Track:
				foreach (var deltaKey in vector3Track.DeltaKeys)
					AddNodeKey(deltaKey.Time, deltaKey.Val, deltaKey);
				break;
			case QuaternionTrack quaternionTrack:
				foreach (var deltaKey in quaternionTrack.DeltaKeys)
					AddNodeKey(deltaKey.Time, deltaKey.Val, deltaKey);
				break;
			case ColorTrack colorTrack:
				foreach (var deltaKey in colorTrack.DeltaKeys)
					AddNodeKey(deltaKey.Time, deltaKey.Val, deltaKey);
				break;
			default:
				throw new NotImplementedException($"Unsupported: {track.GetType()}");
		}
	}

	private void SavePackageToChunk(Package package, string path)
	{
		using var tms = new MemoryStream();
		using var tbw = new BinaryWriter(tms);
		new FrontendChunkWriter(package).Write(tbw);
		tms.Position = 0;
		using var bw = new BinaryWriter(File.Create(path));

		bw.Write(0x30203);
		bw.Write((uint)tms.Length);
		tms.CopyTo(bw.BaseStream);
	}

	private void SavePackageToJson(Package package, string path)
	{
		File.WriteAllText(path, JsonConvert.SerializeObject(package, new JsonSerializerSettings
		{
			Formatting = Formatting.Indented,
			Converters = new List<JsonConverter>
			{
				new StringEnumConverter()
			},
			TypeNameHandling = TypeNameHandling.Auto,
			ReferenceLoopHandling = ReferenceLoopHandling.Error,
			PreserveReferencesHandling = PreserveReferencesHandling.Objects,
			NullValueHandling = NullValueHandling.Ignore
		}));
	}

	private void OpenRecentlySaved(string path, bool fileIsJson)
	{
		var message = fileIsJson ? $"Do you want to open the file?{Environment.NewLine}{Path.GetFileName(path)}" : $"Do you want to open the folder?{Environment.NewLine}{Path.GetDirectoryName(path)}";
		if (MessageBox.Show(message, "Success", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
		{
			var processStartInfo = new ProcessStartInfo
			{
				FileName = path,
				UseShellExecute = true
			};
			if (!fileIsJson)
			{
				processStartInfo.FileName = Path.GetDirectoryName(path);
				processStartInfo.Verb = "open";
			}

			Process.Start(processStartInfo);
		}
	}

	private int CountChildren(IObject<ObjectData> feObject)
	{
		var objects = _currentPackage.Objects.Where(x => feObject?.NameHash == x.Parent?.NameHash && feObject?.Guid == x.Parent?.Guid).ToList();
		var count = objects.Count;

		foreach (var item in objects.Where(x => x is Group))
			count += CountChildren(item);

		return count;
	}

	private void treeView1_AfterSelect(object sender, TreeViewEventArgs e)
	{
		if (_restoringTree || _currentPackage == null)
			return;

		if (e.Node == _rootNode && _rootNode != null)
		{
			viewOutput.SelectedNode = null;
			if (!_restoringTree && (_boundDocument ?? ActiveDocument) is { } owner) owner.SelectedGuid = null;
			LblChildsCount.Text = "子对象：" + _currentPackage.Objects.Where(x => x.Parent == null).Count().ToString("D5") + " | 总计：" + CountChildren(null);
			// The package root has no editable object, so show the panel's empty state.
			SetPropertyTarget(null);
		}
		else if (e.Node?.Tag is RenderTreeNode viewNode)
		{
			if ((_boundDocument ?? ActiveDocument) is { } owner) owner.SelectedGuid = viewNode.GetObject().Guid;
			viewOutput.PrepareLayout();
			viewOutput.SelectedNode = viewNode;
			var wrappedObject = viewNode.GetObject();
			LblItemIndex.Text = "索引：" + _currentPackage.Objects.IndexOf(wrappedObject).ToString("D5");
			if (wrappedObject is Group)
				LblChildsCount.Text = "子对象：" + _currentPackage.Objects.Where(x => wrappedObject.NameHash == x.Parent?.NameHash && wrappedObject.Guid == x.Parent?.Guid).Count().ToString("D5")
					 + " | 总计：" + CountChildren(wrappedObject);
			SetPropertyTarget(wrappedObject switch
			{
				Text t => new TextObjectViewWrapper(t, AppService.Instance.HashResolver),
				Image i => new ImageObjectViewWrapper(i, AppService.Instance.HashResolver),
				ColoredImage ci => new ColoredImageObjectViewWrapper(ci, AppService.Instance.HashResolver),
				_ => new DefaultObjectViewWrapper(wrappedObject, AppService.Instance.HashResolver)
			});
		}
		else if (e.Node?.Tag is Script script)
		{
			viewOutput.SelectedNode = e.Node.Parent?.Tag as RenderTreeNode;
			if ((_boundDocument ?? ActiveDocument) is { } owner) owner.SelectedGuid = viewOutput.SelectedNode?.GetObject().Guid;
			SetPropertyTarget(new DefaultScriptViewWrapper(script, AppService.Instance.HashResolver));
		}
		else
		{
			viewOutput.SelectedNode = FindTreeOwner(e.Node);
			if ((_boundDocument ?? ActiveDocument) is { } owner) owner.SelectedGuid = viewOutput.SelectedNode?.GetObject().Guid;

			SetPropertyTarget(e.Node?.Tag);
			LblItemIndex.Text = null;
			LblChildsCount.Text = null;
		}
		Render();
	}

	/// <summary>
	/// Screen point -> FE point. The preview rectangle can be scaled and centred, so this conversion
	/// must go through the render control instead of assuming a 1:1 640x480 surface.
	/// </summary>
	private PointF ToFePoint(Point screenPoint)
	{
		var logical = viewOutput.ScreenToLogical(screenPoint);
		return new PointF(logical.X - PreviewLayout.LogicalWidth / 2f, logical.Y - PreviewLayout.LogicalHeight / 2f);
	}

	private void viewOutput_MouseMove(object sender, MouseEventArgs e)
	{
		var fe = ToFePoint(e.Location);
		labelCoordDisplay.Text = $"FE: ({fe.X,4:0}, {fe.Y,4:0}) | Real: ({e.X,4:D}, {e.Y,4:D})";
	}

	private void viewOutput_MouseClick(object sender, MouseEventArgs e)
	{
		if (e.Button == MouseButtons.Right)
		{
			var ctxPoint = new Point(e.X, e.Y);
			viewerContextMenu.Show(viewOutput, ctxPoint);
			return;
		}

		// Canvas hit testing and selection are handled once, on mouse-down, by GLRenderControl.
	}

	public static IEnumerable<TreeNode> FlattenTree(TreeView tv)
	{
		return FlattenTree(tv.Nodes);
	}

	public static IEnumerable<TreeNode> FlattenTree(TreeNodeCollection coll)
	{
		return coll.Cast<TreeNode>()
			.Concat(coll.Cast<TreeNode>()
			.SelectMany(x => FlattenTree(x.Nodes)));
	}

	private void FindNode(string text, int direction)
	{
		if (string.IsNullOrEmpty(text))
			return;

		var nodes = FlattenTree(treeView1)
			.ToList();

		var nodesFound = nodes
			.Where(n => n.Text.Contains(text, StringComparison.InvariantCultureIgnoreCase))
			.ToList();

		if (nodesFound.Any())
		{
			_packageViewExtensions.SearchIndex += direction;
			if (_packageViewExtensions.SearchIndex > nodesFound.Count - 1)
				_packageViewExtensions.SearchIndex = 0;
			else if (_packageViewExtensions.SearchIndex < 0)
				_packageViewExtensions.SearchIndex = nodesFound.Count - 1;

			var node = nodesFound.ElementAtOrDefault(_packageViewExtensions.SearchIndex);

			treeView1.SelectedNode = node;
			treeView1.Focus();
		}
	}

	private void CurrentPackageWasModified(string treeKey = null)
	{
		if (_currentPackage == null)
			return;

		var document = _boundDocument ?? _workspace.Active;
		var expanded = FlattenTree(treeView1).Where(n => n.IsExpanded).Select(n => n.Name).ToHashSet();
		var topKey = treeView1.TopNode?.Name;
		_workspace.MarkDirty(document);
		_currentRenderTree = RenderTree.Create(_currentPackage);
		RenderGeometry.Prepare(_currentRenderTree);
		if (document != null) _documentTrees[document] = new RenderCache(_currentRenderTree, document.ModelVersion);
		PopulateTreeView(_currentPackage, _currentRenderTree);
		foreach (var item in FlattenTree(treeView1)) if (expanded.Contains(item.Name)) item.Expand();

		if (!string.IsNullOrWhiteSpace(treeKey))
		{
			var foundNodes = treeView1.Nodes.Find(treeKey, true);
			if (foundNodes.Length > 0)
			{
				treeView1.SelectedNode = foundNodes.First();
			}
		}
		else
		{
			viewOutput.SelectedNode = null;
		}
		if (treeView1.SelectedNode == _rootNode)
			treeView1_AfterSelect(treeView1, new TreeViewEventArgs(_rootNode));
		Render();
		if (!string.IsNullOrEmpty(topKey))
		{
			var top = treeView1.Nodes.Find(topKey, true).FirstOrDefault();
			if (top != null) treeView1.TopNode = top;
		}

		SetFormDetails();
	}

	private void treeView1_MouseDown(object sender, MouseEventArgs e)
	{
		if (e.Button != MouseButtons.Right)
			return;

		TreeNode hit_node = treeView1.GetNodeAt(e.X, e.Y);
		treeView1.SelectedNode = hit_node;
		var ctxPoint = new Point(e.X, e.Y);

		if (hit_node?.Tag is RenderTreeNode)
		{
			objectContextMenu.Show(treeView1, ctxPoint);
		}
		else if (hit_node?.Tag is Script script)
		{
			var viewNode = (RenderTreeNode)hit_node.Parent.Tag;

			toggleScriptMenuItem.Text = viewOutput.IsPreviewOf(viewNode.GetObject(), script.Id) ? "回到初始布局" : "播放";

			scriptContextMenu.Show(treeView1, ctxPoint);
		}
	}

	private void toggleScriptMenuItem_Click(object sender, EventArgs e)
	{
		if (treeView1.SelectedNode?.Tag is Script script)
		{
			var viewNode = (RenderTreeNode)treeView1.SelectedNode.Parent.Tag;
			if (viewOutput.IsPreviewOf(viewNode.GetObject(), script.Id)) viewOutput.ResetPreview();
			else viewOutput.StartPreview(viewNode.GetObject(), script.Id);
		}
	}

	private void renameScriptMenuItem_Click(object sender, EventArgs e)
	{
		if (treeView1.SelectedNode?.Tag is not Script script)
			return;

		var viewNode = (RenderTreeNode)treeView1.SelectedNode.Parent.Tag;
		var objectData = viewNode.GetObject();
		var scripts = objectData.GetScripts();

		var objectInput = _packageViewExtensions.ObjectInput(script.Name, false);

		if (!objectInput.NameHash.HasValue)
			return;

		if (objectInput.Name == script.Name || _packageViewExtensions.NameAlreadyExists(objectInput.Name, objectInput.NameHash.Value, objectData, script))
			return;

		foreach (var chainedScript in scripts)
		{
			if (chainedScript.ChainedId == script.Id)
				chainedScript.ChainedId = objectInput.NameHash;
		}

		script.Name = objectInput.Name;
		script.Id = objectInput.NameHash.Value;

		CurrentPackageWasModified(PackageViewExtensions.GetObjectTreeKey(objectData));
	}

	private void backgroundColorMenuItem_Click(object sender, EventArgs e)
	{
		var colorDialog = new ColorDialog();
		if (colorDialog.ShowDialog() != DialogResult.OK)
			return;

		viewOutput.BackgroundColor = new Color4(colorDialog.Color.B, colorDialog.Color.G, colorDialog.Color.R, colorDialog.Color.A);

		Render();
	}

	private void renameToolStripMenuItem_Click(object sender, EventArgs e)
	{
		if (treeView1.SelectedNode?.Tag is not RenderTreeNode node)
			return;

		var selectedObject = node.GetObject();

		if (selectedObject is null)
			return;

		var objectInput = _packageViewExtensions.ObjectInput(selectedObject.Name, false);

		if (!objectInput.NameHash.HasValue)
			return;

		if (objectInput.Name == selectedObject.Name || _packageViewExtensions.NameAlreadyExists(objectInput.Name, objectInput.NameHash.Value, selectedObject))
			return;

		selectedObject.Name = objectInput.Name;
		selectedObject.NameHash = objectInput.NameHash.Value;
		AppService.Instance.HashResolver.AddUserKey(selectedObject.Name, selectedObject.NameHash);

		CurrentPackageWasModified(PackageViewExtensions.GetObjectTreeKey(selectedObject));
	}

	private void deleteToolStripMenuItem_Click(object sender, EventArgs e)
	{
		if (treeView1.SelectedNode?.Tag is not RenderTreeNode node)
			return;

		var selectedObject = node.GetObject();

		if (selectedObject is null)
			return;

		var parentKey = PackageViewExtensions.GetObjectTreeKey(selectedObject.Parent);
		_packageViewExtensions.DeleteObject(selectedObject);
		CurrentPackageWasModified(parentKey);
	}

	private void duplicateToolStripMenuItem_Click(object sender, EventArgs e)
	{
		if (treeView1.SelectedNode?.Tag is not RenderTreeNode node)
			return;

		var selectedObject = node.GetObject();

		if (selectedObject is null)
			return;

		var lastChild = _packageViewExtensions.FindLastChild(selectedObject);
		var position = _currentPackage.Objects.IndexOf(lastChild) + 1;
		var newObject = _packageViewExtensions.CopyObject(selectedObject, selectedObject.Parent, position);

		if (newObject is null)
			return;

		AppService.Instance.HashResolver.AddUserKey(newObject.Name, newObject.NameHash);

		CurrentPackageWasModified(PackageViewExtensions.GetObjectTreeKey(newObject));
	}

	private void duplicateUntilToolStripMenuItem_Click(object sender, EventArgs e)
	{
		if (treeView1.SelectedNode?.Tag is not RenderTreeNode node)
			return;

		var selectedObject = node.GetObject();

		if (selectedObject is null || string.IsNullOrWhiteSpace(selectedObject.Name))
			return;

		var index = selectedObject.Name.Length;
		while (index > 0 && char.IsDigit(selectedObject.Name[index - 1]))
			index--;

		var currentObjectName = selectedObject.Name[..index];
		var currentObjectNumber = selectedObject.Name[index..];

		var objectInput = _packageViewExtensions.ObjectInput(currentObjectNumber, false);

		if (!objectInput.NameHash.HasValue)
			return;

		if (!int.TryParse(objectInput.Name, out var untilNumber))
			return;

		var objects = new List<IObject<ObjectData>>();

		var lastChild = _packageViewExtensions.FindLastChild(selectedObject);
		var position = _currentPackage.Objects.IndexOf(lastChild) + 1;

		var initialNumber = index == selectedObject.Name.Length ? 0 : int.Parse(currentObjectNumber);

		for (int i = untilNumber; i > initialNumber; i--)
		{
			var newObject = _packageViewExtensions.CopyObject(selectedObject, selectedObject.Parent, position, currentObjectName, currentObjectNumber, i);

			if (newObject is null)
				continue;

			objects.Add(newObject);
		}

		if (!objects.Any())
			return;

		foreach (var newObject in objects)
		{
			AppService.Instance.HashResolver.AddUserKey(newObject.Name, newObject.NameHash);
		}

		CurrentPackageWasModified(PackageViewExtensions.GetObjectTreeKey(objects.Last()));
	}

	private void cutToolStripMenuItem_Click(object sender, EventArgs e)
	{
		if (treeView1.SelectedNode?.Tag is not RenderTreeNode node)
			return;

		_packageViewExtensions.ObjectSelected = node.GetObject();
		_packageViewExtensions.ShouldCopyObject = false;
	}

	private void copyToolStripMenuItem_Click(object sender, EventArgs e)
	{
		if (treeView1.SelectedNode?.Tag is not RenderTreeNode node)
			return;

		_packageViewExtensions.ObjectSelected = node.GetObject();
		_packageViewExtensions.ShouldCopyObject = true;
	}

	private void pasteToolStripMenuItem_Click(object sender, EventArgs e)
	{
		if (_packageViewExtensions.ObjectSelected is null)
			return;

		if (treeView1.SelectedNode?.Tag is not RenderTreeNode node)
			return;

		var selectedObject = node.GetObject();

		if (selectedObject is null)
			return;

		var treeKey = PackageViewExtensions.GetObjectTreeKey(selectedObject);

		if (_packageViewExtensions.ShouldCopyObject)
		{
			var lastChild = _packageViewExtensions.FindLastChild(selectedObject);
			var newIndex = _currentPackage.Objects.IndexOf(lastChild) + 1;
			var parent = selectedObject is Group && selectedObject == lastChild ? selectedObject : selectedObject.Parent;

			var newObject = _packageViewExtensions.CopyObject(_packageViewExtensions.ObjectSelected, parent, newIndex);

			if (newObject is null)
				return;

			AppService.Instance.HashResolver.AddUserKey(newObject.Name, newObject.NameHash);
			treeKey = PackageViewExtensions.GetObjectTreeKey(newObject);
		}
		else
		{
			if (_packageViewExtensions.ObjectSelected == selectedObject)
			{
				_packageViewExtensions.ObjectSelected = null;
				return;
			}

			treeKey = PackageViewExtensions.GetObjectTreeKey(_packageViewExtensions.ObjectSelected);
			var lastChild = _packageViewExtensions.FindLastChild(selectedObject);
			var newIndex = _currentPackage.Objects.IndexOf(lastChild) + 1;
			var oldIndex = _currentPackage.Objects.IndexOf(_packageViewExtensions.ObjectSelected);

			if (oldIndex == newIndex)
				return;

			var newParent = selectedObject is Group && selectedObject == lastChild ? selectedObject : selectedObject.Parent;
			for (var ancestor = newParent; ancestor != null; ancestor = ancestor.Parent)
				if (ReferenceEquals(ancestor, _packageViewExtensions.ObjectSelected))
				{
					MessageBox.Show(this, "不能把组移动到自己的子组中。", "无法移动");
					return;
				}
			_packageViewExtensions.ObjectSelected.Parent = newParent;
			_packageViewExtensions.MoveItem(oldIndex, newIndex);
			_packageViewExtensions.MoveChildren(_packageViewExtensions.ObjectSelected);

			_packageViewExtensions.ObjectSelected = null;
		}

		CurrentPackageWasModified(treeKey);
	}


	private void BtnPrevious_Click(object sender, EventArgs e)
	{
		FindNode(TxtSearch.Text, -1);
	}

	private void BtnNext_Click(object sender, EventArgs e)
	{
		FindNode(TxtSearch.Text, +1);
	}

	private void TxtSearch_Leave(object sender, EventArgs e)
	{
		if (_packageViewExtensions.SearchText != TxtSearch.Text)
		{
			_packageViewExtensions.SearchText = TxtSearch.Text;
			_packageViewExtensions.SearchIndex = -1;
		}

		if (_workspace?.Active != null && _leftMode == WorkspaceLeftMode.ObjectTree)
		{
			_workspace.Active.SearchText = TxtSearch.Text;
			_workspace.Active.SearchIndex = _packageViewExtensions.SearchIndex;
		}
	}

	private void moveUpToolStripMenuItem_Click(object sender, EventArgs e)
	{
		if (treeView1.SelectedNode?.Tag is not RenderTreeNode node)
			return;

		var selectedObject = node.GetObject();

		if (selectedObject is null)
			return;

		var parents = _currentPackage.Objects.FindAll(x => x.Parent?.NameHash == selectedObject.Parent?.NameHash && x.Parent?.Guid == selectedObject.Parent?.Guid);
		var indexInParents = parents.IndexOf(selectedObject);
		var newIndexInParents = indexInParents - 1;

		if (newIndexInParents > parents.Count - 1 || newIndexInParents < 0)
			return;

		var lastChild = parents.ElementAt(newIndexInParents);

		var newIndex = _currentPackage.Objects.IndexOf(lastChild);
		var oldIndex = _currentPackage.Objects.IndexOf(selectedObject);

		_packageViewExtensions.MoveItem(oldIndex, newIndex);
		_packageViewExtensions.MoveChildren(selectedObject);

		CurrentPackageWasModified(PackageViewExtensions.GetObjectTreeKey(selectedObject));
	}

	private void moveDownToolStripMenuItem_Click(object sender, EventArgs e)
	{
		if (treeView1.SelectedNode?.Tag is not RenderTreeNode node)
			return;

		var selectedObject = node.GetObject();

		if (selectedObject is null)
			return;

		var parents = _currentPackage.Objects.FindAll(x => x.Parent?.NameHash == selectedObject.Parent?.NameHash && x.Parent?.Guid == selectedObject.Parent?.Guid);
		var indexInParents = parents.IndexOf(selectedObject);
		var newIndexInParents = indexInParents + 1;

		if (newIndexInParents > parents.Count - 1 || newIndexInParents < 0)
			return;

		var objectInNewIndex = parents.ElementAt(newIndexInParents);
		var lastChild = _packageViewExtensions.FindLastChild(objectInNewIndex);

		var oldIndex = _currentPackage.Objects.IndexOf(selectedObject);
		var newIndex = _currentPackage.Objects.IndexOf(lastChild) + 1;

		_packageViewExtensions.MoveItem(oldIndex, newIndex);
		_packageViewExtensions.MoveChildren(selectedObject);

		CurrentPackageWasModified(PackageViewExtensions.GetObjectTreeKey(selectedObject));
	}

	private void moveUpScriptMenuItem_Click(object sender, EventArgs e)
	{
		MoveScript(-1);
	}

	private void moveDownScriptMenuItem_Click(object sender, EventArgs e)
	{
		MoveScript(1);
	}


	private void MoveScript(int position)
	{
		if (treeView1.SelectedNode?.Tag is not Script script)
			return;

		var viewNode = (RenderTreeNode)treeView1.SelectedNode.Parent.Tag;
		var selectedObject = viewNode.GetObject();

		if (selectedObject is null || selectedObject is not BaseObject baseObject)
			return;

		var currentItemIndex = baseObject.Scripts.IndexOf(script as BaseObjectScript);
		var currentItemNewIndex = currentItemIndex + position;
		if (currentItemNewIndex > baseObject.Scripts.Count - 1 || currentItemNewIndex < 0)
			return;

		var unrelatedScript = baseObject.Scripts.ElementAt(currentItemNewIndex);

		baseObject.Scripts[currentItemNewIndex] = script as BaseObjectScript;
		baseObject.Scripts[currentItemIndex] = unrelatedScript;

		CurrentPackageWasModified(PackageViewExtensions.GetObjectTreeKey(selectedObject));
	}

	private void orderGroupToolStripMenu_Click(object sender, EventArgs e)
	{
		if (treeView1.SelectedNode?.Tag is not RenderTreeNode node)
			return;

		var selectedObject = node.GetObject();

		OrderGroups(selectedObject, false);

		CurrentPackageWasModified(PackageViewExtensions.GetObjectTreeKey(selectedObject));
	}

	private void orderGroupChildsToolStripMenu_Click(object sender, EventArgs e)
	{
		if (treeView1.SelectedNode?.Tag is not RenderTreeNode node)
			return;

		var selectedObject = node.GetObject();

		OrderGroups(selectedObject, true);

		CurrentPackageWasModified(PackageViewExtensions.GetObjectTreeKey(selectedObject));
	}

	private void OrderGroups(IObject<ObjectData> node, bool orderChilds)
	{
		if (node is null || node is not Group)
			return;

		var feObjects = _currentPackage.Objects
			.Where(x => x.Parent?.NameHash == node.NameHash && x.Parent?.Guid == node.Guid)
			.OrderBy(x => x.Name, new NaturalStringComparer()).ThenBy(x => x.NameHash).ThenBy(x => x.Guid)
			.ToList();

		foreach (var feObject in feObjects)
		{
			_currentPackage.Objects.Remove(feObject);
		}

		var parentIndex = _currentPackage.Objects.IndexOf(node) + 1;

		_currentPackage.Objects.InsertRange(parentIndex, feObjects);

		foreach (var item in feObjects)
		{
			if (item is Group)
			{
				_packageViewExtensions.MoveChildren(item);

				if (orderChilds)
					OrderGroups(item, true);
			}
		}
	}
}

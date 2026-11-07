
using System.Windows.Forms;

namespace FEngViewer
{
	partial class PackageView
	{
		/// <summary>
		/// Required designer variable.
		/// </summary>
		private System.ComponentModel.IContainer components = null;

		/// <summary>
		/// Clean up any resources being used.
		/// </summary>
		/// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
		protected override void Dispose(bool disposing)
		{
			if (disposing && (components != null))
			{
				components.Dispose();
			}
			base.Dispose(disposing);
		}

		#region Windows Form Designer generated code

		/// <summary>
		/// Required method for Designer support - do not modify
		/// the contents of this method with the code editor.
		/// </summary>
		private void InitializeComponent()
		{
			this.components = new System.ComponentModel.Container();
			this.viewOutput = new GLRenderControl();
			this.labelCoordDisplay = new Label();
			this.colorDialog1 = new ColorDialog();
			this.menuStrip1 = new MenuStrip();
			this.FileMenuItem = new ToolStripMenuItem();
			this.objectContextMenu = new ContextMenuStrip(this.components);
			this.renameToolStripMenuItem = new ToolStripMenuItem();
			this.moveUpToolStripMenuItem = new ToolStripMenuItem();
			this.moveDownToolStripMenuItem = new ToolStripMenuItem();
			this.duplicateToolStripMenuItem = new ToolStripMenuItem();
			this.duplicateUntilToolStripMenuItem = new ToolStripMenuItem();
			this.copyToolStripMenuItem = new ToolStripMenuItem();
			this.cutToolStripMenuItem = new ToolStripMenuItem();
			this.pasteToolStripMenuItem = new ToolStripMenuItem();
			this.deleteToolStripMenuItem = new ToolStripMenuItem();
			this.orderGroupToolStripMenu = new ToolStripMenuItem();
			this.orderGroupChildsToolStripMenu = new ToolStripMenuItem();
			this.scriptContextMenu = new ContextMenuStrip(this.components);
			this.toggleScriptMenuItem = new ToolStripMenuItem();
			this.renameScriptMenuItem = new ToolStripMenuItem();
			this.moveUpScriptMenuItem = new ToolStripMenuItem();
			this.moveDownScriptMenuItem = new ToolStripMenuItem();
			this.BtnNext = new Button();
			this.BtnPrevious = new Button();
			this.TxtSearch = new TextBox();
			this.objectPropertyGrid = new PropertyGrid();
			this.treeView1 = new TreeView();
			this.groupBox1 = new GroupBox();
			this.LblChildsCount = new Label();
			this.LblItemIndex = new Label();
			this.viewerContextMenu = new ContextMenuStrip(this.components);
			this.backgroundColorMenuItem = new ToolStripMenuItem();
			this.menuStrip1.SuspendLayout();
			this.objectContextMenu.SuspendLayout();
			this.scriptContextMenu.SuspendLayout();
			this.groupBox1.SuspendLayout();
			this.viewerContextMenu.SuspendLayout();
			this.SuspendLayout();
			// 
			// viewOutput
			// 
			this.viewOutput.BackColor = System.Drawing.Color.Black;
			this.viewOutput.Name = "viewOutput";
			this.viewOutput.SelectedNode = null;
			this.viewOutput.Size = new System.Drawing.Size(640, 480);
			this.viewOutput.TabIndex = 0;
			this.viewOutput.TabStop = false;
			this.viewOutput.MouseClick += this.viewOutput_MouseClick;
			this.viewOutput.MouseMove += this.viewOutput_MouseMove;
			// 
			// labelCoordDisplay
			// 
			this.labelCoordDisplay.AutoSize = true;
			this.labelCoordDisplay.Name = "labelCoordDisplay";
			this.labelCoordDisplay.Size = new System.Drawing.Size(72, 15);
			this.labelCoordDisplay.TabIndex = 2;
			this.labelCoordDisplay.Text = "X:    0   Y:    0";
			// 
			// menuStrip1
			// 
			this.menuStrip1.ImageScalingSize = new System.Drawing.Size(20, 20);
			this.menuStrip1.Items.AddRange(new ToolStripItem[] { this.FileMenuItem });
			this.menuStrip1.Location = new System.Drawing.Point(0, 0);
			this.menuStrip1.Name = "menuStrip1";
			this.menuStrip1.Size = new System.Drawing.Size(1484, 24);
			this.menuStrip1.TabIndex = 4;
			this.menuStrip1.Text = "menuStrip1";
			// 
			// FileMenuItem
			// 
			this.FileMenuItem.Name = "FileMenuItem";
			this.FileMenuItem.Size = new System.Drawing.Size(37, 20);
			this.FileMenuItem.Text = "File";
			// 
			// objectContextMenu
			// 
			this.objectContextMenu.ImageScalingSize = new System.Drawing.Size(20, 20);
			this.objectContextMenu.Items.AddRange(new ToolStripItem[] { this.renameToolStripMenuItem, this.moveUpToolStripMenuItem, this.moveDownToolStripMenuItem, this.duplicateToolStripMenuItem, this.duplicateUntilToolStripMenuItem, this.copyToolStripMenuItem, this.cutToolStripMenuItem, this.pasteToolStripMenuItem, this.deleteToolStripMenuItem, this.orderGroupToolStripMenu, this.orderGroupChildsToolStripMenu });
			this.objectContextMenu.Name = "objectContextMenu";
			this.objectContextMenu.Size = new System.Drawing.Size(209, 246);
			// 
			// renameToolStripMenuItem
			// 
			this.renameToolStripMenuItem.Name = "renameToolStripMenuItem";
			this.renameToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.R;
			this.renameToolStripMenuItem.Size = new System.Drawing.Size(208, 22);
			this.renameToolStripMenuItem.Text = "Rename";
			this.renameToolStripMenuItem.Click += this.renameToolStripMenuItem_Click;
			// 
			// moveUpToolStripMenuItem
			// 
			this.moveUpToolStripMenuItem.Name = "moveUpToolStripMenuItem";
			this.moveUpToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.Up;
			this.moveUpToolStripMenuItem.Size = new System.Drawing.Size(208, 22);
			this.moveUpToolStripMenuItem.Text = "Move up";
			this.moveUpToolStripMenuItem.Click += this.moveUpToolStripMenuItem_Click;
			// 
			// moveDownToolStripMenuItem
			// 
			this.moveDownToolStripMenuItem.Name = "moveDownToolStripMenuItem";
			this.moveDownToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.Down;
			this.moveDownToolStripMenuItem.Size = new System.Drawing.Size(208, 22);
			this.moveDownToolStripMenuItem.Text = "Move down";
			this.moveDownToolStripMenuItem.Click += this.moveDownToolStripMenuItem_Click;
			// 
			// duplicateToolStripMenuItem
			// 
			this.duplicateToolStripMenuItem.Name = "duplicateToolStripMenuItem";
			this.duplicateToolStripMenuItem.Size = new System.Drawing.Size(208, 22);
			this.duplicateToolStripMenuItem.Text = "Duplicate";
			this.duplicateToolStripMenuItem.Click += this.duplicateToolStripMenuItem_Click;
			// 
			// duplicateUntilToolStripMenuItem
			// 
			this.duplicateUntilToolStripMenuItem.Name = "duplicateUntilToolStripMenuItem";
			this.duplicateUntilToolStripMenuItem.Size = new System.Drawing.Size(208, 22);
			this.duplicateUntilToolStripMenuItem.Text = "Duplicate until...";
			this.duplicateUntilToolStripMenuItem.Click += this.duplicateUntilToolStripMenuItem_Click;
			// 
			// copyToolStripMenuItem
			// 
			this.copyToolStripMenuItem.Name = "copyToolStripMenuItem";
			this.copyToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.C;
			this.copyToolStripMenuItem.Size = new System.Drawing.Size(208, 22);
			this.copyToolStripMenuItem.Text = "Copy";
			this.copyToolStripMenuItem.Click += this.copyToolStripMenuItem_Click;
			// 
			// cutToolStripMenuItem
			// 
			this.cutToolStripMenuItem.Name = "cutToolStripMenuItem";
			this.cutToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.X;
			this.cutToolStripMenuItem.Size = new System.Drawing.Size(208, 22);
			this.cutToolStripMenuItem.Text = "Cut";
			this.cutToolStripMenuItem.Click += this.cutToolStripMenuItem_Click;
			// 
			// pasteToolStripMenuItem
			// 
			this.pasteToolStripMenuItem.Name = "pasteToolStripMenuItem";
			this.pasteToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.V;
			this.pasteToolStripMenuItem.Size = new System.Drawing.Size(208, 22);
			this.pasteToolStripMenuItem.Text = "Paste";
			this.pasteToolStripMenuItem.Click += this.pasteToolStripMenuItem_Click;
			// 
			// deleteToolStripMenuItem
			// 
			this.deleteToolStripMenuItem.Name = "deleteToolStripMenuItem";
			this.deleteToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.D;
			this.deleteToolStripMenuItem.Size = new System.Drawing.Size(208, 22);
			this.deleteToolStripMenuItem.Text = "Delete";
			this.deleteToolStripMenuItem.Click += this.deleteToolStripMenuItem_Click;
			// 
			// orderGroupToolStripMenu
			// 
			this.orderGroupToolStripMenu.Name = "orderGroupToolStripMenu";
			this.orderGroupToolStripMenu.Size = new System.Drawing.Size(208, 22);
			this.orderGroupToolStripMenu.Text = "Order group";
			this.orderGroupToolStripMenu.Click += this.orderGroupToolStripMenu_Click;
			// 
			// orderGroupChildsToolStripMenu
			// 
			this.orderGroupChildsToolStripMenu.Name = "orderGroupChildsToolStripMenu";
			this.orderGroupChildsToolStripMenu.Size = new System.Drawing.Size(208, 22);
			this.orderGroupChildsToolStripMenu.Text = "Order group and children";
			this.orderGroupChildsToolStripMenu.Click += this.orderGroupChildsToolStripMenu_Click;
			// 
			// scriptContextMenu
			// 
			this.scriptContextMenu.ImageScalingSize = new System.Drawing.Size(20, 20);
			this.scriptContextMenu.Items.AddRange(new ToolStripItem[] { this.toggleScriptMenuItem, this.renameScriptMenuItem, this.moveUpScriptMenuItem, this.moveDownScriptMenuItem });
			this.scriptContextMenu.Name = "scriptContextMenu";
			this.scriptContextMenu.Size = new System.Drawing.Size(138, 92);
			// 
			// toggleScriptMenuItem
			// 
			this.toggleScriptMenuItem.Name = "toggleScriptMenuItem";
			this.toggleScriptMenuItem.Size = new System.Drawing.Size(137, 22);
			this.toggleScriptMenuItem.Text = "Start/Stop";
			this.toggleScriptMenuItem.Click += this.toggleScriptMenuItem_Click;
			// 
			// renameScriptMenuItem
			// 
			this.renameScriptMenuItem.Name = "renameScriptMenuItem";
			this.renameScriptMenuItem.Size = new System.Drawing.Size(137, 22);
			this.renameScriptMenuItem.Text = "Rename";
			this.renameScriptMenuItem.Click += this.renameScriptMenuItem_Click;
			// 
			// moveUpScriptMenuItem
			// 
			this.moveUpScriptMenuItem.Name = "moveUpScriptMenuItem";
			this.moveUpScriptMenuItem.Size = new System.Drawing.Size(137, 22);
			this.moveUpScriptMenuItem.Text = "Move up";
			this.moveUpScriptMenuItem.Click += this.moveUpScriptMenuItem_Click;
			// 
			// moveDownScriptMenuItem
			// 
			this.moveDownScriptMenuItem.Name = "moveDownScriptMenuItem";
			this.moveDownScriptMenuItem.Size = new System.Drawing.Size(137, 22);
			this.moveDownScriptMenuItem.Text = "Move down";
			this.moveDownScriptMenuItem.Click += this.moveDownScriptMenuItem_Click;
			// 
			// BtnNext
			// 
			this.BtnNext.Name = "BtnNext";
			this.BtnNext.Size = new System.Drawing.Size(195, 23);
			this.BtnNext.TabIndex = 7;
			this.BtnNext.Text = "Next";
			this.BtnNext.UseVisualStyleBackColor = true;
			this.BtnNext.Click += this.BtnNext_Click;
			// 
			// BtnPrevious
			// 
			this.BtnPrevious.Name = "BtnPrevious";
			this.BtnPrevious.Size = new System.Drawing.Size(194, 23);
			this.BtnPrevious.TabIndex = 7;
			this.BtnPrevious.Text = "Previous";
			this.BtnPrevious.UseVisualStyleBackColor = true;
			this.BtnPrevious.Click += this.BtnPrevious_Click;
			// 
			// TxtSearch
			// 
			this.TxtSearch.Name = "TxtSearch";
			this.TxtSearch.PlaceholderText = "Search";
			this.TxtSearch.Size = new System.Drawing.Size(395, 23);
			this.TxtSearch.TabIndex = 7;
			this.TxtSearch.Leave += this.TxtSearch_Leave;
			// 
			// objectPropertyGrid
			// 
			this.objectPropertyGrid.Name = "objectPropertyGrid";
			this.objectPropertyGrid.Size = new System.Drawing.Size(401, 755);
			this.objectPropertyGrid.TabIndex = 5;
			// 
			// treeView1
			// 
			this.treeView1.Dock = DockStyle.Fill;
			this.treeView1.Name = "treeView1";
			this.treeView1.Size = new System.Drawing.Size(401, 691);
			this.treeView1.TabIndex = 2;
			this.treeView1.AfterSelect += this.treeView1_AfterSelect;
			this.treeView1.MouseDown += this.treeView1_MouseDown;
			// 
			// groupBox1
			// 
			this.groupBox1.AutoSize = true;
			this.groupBox1.Controls.Add(this.LblChildsCount);
			this.groupBox1.Controls.Add(this.LblItemIndex);
			this.groupBox1.Name = "groupBox1";
			this.groupBox1.Size = new System.Drawing.Size(401, 53);
			this.groupBox1.TabIndex = 9;
			this.groupBox1.TabStop = false;
			// 
			// LblChildsCount
			// 
			this.LblChildsCount.AutoSize = true;
			this.LblChildsCount.Location = new System.Drawing.Point(6, 19);
			this.LblChildsCount.Name = "LblChildsCount";
			this.LblChildsCount.Size = new System.Drawing.Size(88, 15);
			this.LblChildsCount.TabIndex = 1;
			this.LblChildsCount.Text = "子对象：00000";
			// 
			// LblItemIndex
			// 
			this.LblItemIndex.Anchor = AnchorStyles.Top | AnchorStyles.Right;
			this.LblItemIndex.AutoSize = true;
			this.LblItemIndex.Location = new System.Drawing.Point(323, 19);
			this.LblItemIndex.Name = "LblItemIndex";
			this.LblItemIndex.Size = new System.Drawing.Size(72, 15);
			this.LblItemIndex.TabIndex = 0;
			this.LblItemIndex.Text = "索引：00000";
			this.LblItemIndex.TextAlign = System.Drawing.ContentAlignment.TopRight;
			// 
			// viewerContextMenu
			// 
			this.viewerContextMenu.Items.AddRange(new ToolStripItem[] { this.backgroundColorMenuItem });
			this.viewerContextMenu.Name = "viewerContextMenu";
			this.viewerContextMenu.Size = new System.Drawing.Size(169, 26);
			// 
			// backgroundColorMenuItem
			// 
			this.backgroundColorMenuItem.Name = "backgroundColorMenuItem";
			this.backgroundColorMenuItem.Size = new System.Drawing.Size(168, 22);
			this.backgroundColorMenuItem.Text = "Background color";
			this.backgroundColorMenuItem.Click += this.backgroundColorMenuItem_Click;
			// 
			// PackageView
			// 
			this.AutoScaleMode = AutoScaleMode.Inherit;
			this.ClientSize = new System.Drawing.Size(1484, 861);
			this.Controls.Add(this.menuStrip1);
			this.MinimumSize = new System.Drawing.Size(820, 560);
			this.Name = "PackageView";
			this.Text = "FEngEditor";
			this.Load += this.PackageView_Load;
			this.menuStrip1.ResumeLayout(false);
			this.menuStrip1.PerformLayout();
			this.objectContextMenu.ResumeLayout(false);
			this.scriptContextMenu.ResumeLayout(false);
			this.groupBox1.ResumeLayout(false);
			this.groupBox1.PerformLayout();
			this.viewerContextMenu.ResumeLayout(false);
			this.ResumeLayout(false);
			this.PerformLayout();
		}

		#endregion
		private GLRenderControl viewOutput;
		private System.Windows.Forms.ColorDialog colorDialog1;
		private System.Windows.Forms.Label labelCoordDisplay;
		private System.Windows.Forms.MenuStrip menuStrip1;
		private System.Windows.Forms.ContextMenuStrip objectContextMenu;
		private System.Windows.Forms.ContextMenuStrip scriptContextMenu;
		private System.Windows.Forms.ToolStripMenuItem toggleScriptMenuItem;
		private ToolStripMenuItem deleteToolStripMenuItem;
		private ToolStripMenuItem FileMenuItem;
		private ToolStripMenuItem duplicateToolStripMenuItem;
		private ToolStripMenuItem copyToolStripMenuItem;
		private ToolStripMenuItem pasteToolStripMenuItem;
		private ToolStripMenuItem cutToolStripMenuItem;
		private ToolStripMenuItem renameToolStripMenuItem;
		internal TreeView treeView1;
		private PropertyGrid objectPropertyGrid;
		private TextBox TxtSearch;
		private Button BtnNext;
		private Button BtnPrevious;
		private ToolStripMenuItem moveUpToolStripMenuItem;
		private ToolStripMenuItem moveDownToolStripMenuItem;
		private ToolStripMenuItem editToolStripMenuItem;
		private ToolStripMenuItem renameScriptMenuItem;
		private ToolStripMenuItem moveUpScriptMenuItem;
		private ToolStripMenuItem moveDownScriptMenuItem;
		private Button BtnBackgroundColorPicker;
		private ContextMenuStrip viewerContextMenu;
		private ToolStripMenuItem backgroundColorMenuItem;
		private ToolStripMenuItem orderGroupToolStripMenu;
		private ToolStripMenuItem orderGroupChildsToolStripMenu;
		private GroupBox groupBox1;
		private Label LblChildsCount;
		private Label LblItemIndex;
		private ToolStripMenuItem duplicateUntilToolStripMenuItem;
	}
}

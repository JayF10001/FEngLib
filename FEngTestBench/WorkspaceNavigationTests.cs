using System;
using System.IO;
using System.Linq;
using FEngLib.Editing;
using FEngLib.Objects;
using FEngLib.Packages;
using FEngViewer;

namespace FEngTestBench;

/// <summary>Exercises the actual UI-neutral viewer state layer without an OpenGL context.</summary>
internal static class WorkspaceNavigationTests
{
    public static int Run(string directory)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
            checks++;
        }
        try
        {
            var root = Path.Combine(Path.GetFullPath(directory), "workspace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "A"));
            Directory.CreateDirectory(Path.Combine(root, "B"));
            var a = Path.Combine(root, "A", "Same.fng");
            var b = Path.Combine(root, "B", "Same.fng");
            File.WriteAllText(a, "fixture"); File.WriteAllText(b, "fixture");
            var prompts = new Prompts();
            var workspace = new DocumentWorkspace(prompts);
            var first = workspace.Import(new[] { a }, false, "A").Added.Single();
            var second = workspace.Import(new[] { b }, false, "B").Added.Single();
            Check(workspace.Active == null && workspace.DocumentCount == 0, "Startup must show the global page.");
            Check(workspace.FileCount == 2 && workspace.Groups.Count == 2, "Same names in different sources collide.");
            Check(workspace.Import(new[] { Path.Combine(root, "A", ".", "Same.fng") }, false, "A").Existing.Count == 1,
                "Normalized paths are not deduplicated.");
            var docA = workspace.OpenFile(first);
            var package = docA.Package;
            var obj = FrontendObjectFactory.Create(package, ObjectType.Group, "CHANGED");
            workspace.MarkDirty(docA);
            docA.SelectedGuid = obj.Guid;
            docA.SelectedTreeKey = "object-key"; docA.SearchText = "CHANGED";
            docA.CanvasZoom = 2; docA.CanvasFit = false;
            docA.PreviewState = new PreviewPlaybackState { ObjectGuid = obj.Guid, ScriptId = 123, Elapsed = 400, Playing = true };
            Check(docA.IsDirty, "Fixture edit is not dirty.");
            var reads = prompts.Reads;
            Check(workspace.ShowWorkspace() && workspace.Active == null, "Workspace activation retained an editing document.");
            Check(workspace.DocumentCount == 1 && docA.IsDirty && ReferenceEquals(package, docA.Package), "Workspace discarded model/dirty state.");
            Check(ReferenceEquals(workspace.FindDocument(first.Path)?.Package, package), "Preview lookup does not return the edited memory model.");
            Check(workspace.Activate(docA) && prompts.Reads == reads, "Returning to a document reread its disk model.");
            Check(docA.SelectedGuid == obj.Guid && docA.SelectedTreeKey == "object-key" && docA.SearchText == "CHANGED" &&
                docA.CanvasZoom == 2 && !docA.CanvasFit && docA.PreviewState.Elapsed == 400, "Editor state was lost across workspace navigation.");
            var docB = workspace.OpenFile(second);
            Check(docB != docA && !ReferenceEquals(docB.Package, package) && !docB.IsDirty && docA.IsDirty,
                "Document model or dirty state leaked between tabs.");
            workspace.ShowWorkspace();
            workspace.Move(1, 0);
            Check(workspace.Documents[0] == docB && workspace.Active == null, "Reordering documents removed the global page.");
            prompts.FailRead = true;
            Check(!workspace.Reload(docA) && docA.IsDirty && ReferenceEquals(docA.Package, package), "Failed reload destroyed unsaved data.");
            docB.InvalidateCleanModel();
            Check(!workspace.Activate(docB) && workspace.Active == null && docA.IsDirty, "Load failure polluted global page state.");
            prompts.FailRead = false;
            workspace.Activate(docA);
            EventHandler<WorkspaceDocument> reject = (_, doc) =>
            {
                if (ReferenceEquals(doc, docB)) throw new InvalidOperationException("binding fixture failure");
            };
            workspace.ActiveChanged += reject;
            Check(!workspace.Activate(docB) && workspace.Active == docA, "Binding failure did not roll back Active.");
            Check(workspace.OpenFile(second) == null && workspace.Active == docA, "Open reported success after failed binding.");
            workspace.ActiveChanged -= reject;
            var oldHistory = docA.History;
            EventHandler<WorkspaceDocument> rejectReload = (_, doc) =>
            {
                if (ReferenceEquals(doc, docA) && !ReferenceEquals(doc.Package, package))
                    throw new InvalidOperationException("reload binding fixture failure");
            };
            workspace.ActiveChanged += rejectReload;
            Check(!workspace.Reload(docA) && ReferenceEquals(docA.Package, package) && ReferenceEquals(docA.History, oldHistory) &&
                docA.IsDirty && workspace.Active == docA, "Reload binding failure destroyed the model, history or dirty state.");
            workspace.ActiveChanged -= rejectReload;
            workspace.ShowWorkspace();
            prompts.CloseAnswer = SavePromptResult.Cancel;
            Check(!workspace.CloseAll() && workspace.Active == null && workspace.Documents.Contains(docA) && docA.IsDirty,
                "Cancelled close all changed the global page or discarded a dirty model.");
            Check(!workspace.Close(docA) && workspace.Documents.Contains(docA) && docA.IsDirty, "Cancelled close discarded the model.");
            prompts.CloseAnswer = SavePromptResult.Discard;
            Check(workspace.CloseOthers(docA) && workspace.DocumentCount == 1 && workspace.Active == null, "Close others affected workspace navigation.");
            workspace.Activate(docA);
            Check(workspace.Close(docA) && workspace.Active == null && workspace.DocumentCount == 0 && workspace.FileCount == 2,
                "Last document close lost the index or left an editing document active.");
            workspace.OpenFile(second);
            Check(workspace.CloseAll() && workspace.Active == null && workspace.DocumentCount == 0 && workspace.FileCount == 2,
                "Close all removed workspace files.");
            var rejectedPath = Path.Combine(root, "Rejected.fng");
            File.WriteAllText(rejectedPath, "fixture");
            var rejectedFile = workspace.Import(new[] { rejectedPath }, false, "Rejected").Added.Single();
            workspace.ActiveChanged += (_, doc) =>
            {
                if (doc?.MatchesPath(rejectedPath) == true) throw new InvalidOperationException("new binding fixture failure");
            };
            Check(workspace.OpenFile(rejectedFile) == null && workspace.Active == null && workspace.DocumentCount == 0 &&
                workspace.FindFile(rejectedPath) != null, "Failed new-document binding left a broken tab or removed its index entry.");
            Check(workspace.OpenFirstAvailable(new[] { rejectedFile }, out var failures) == null && failures == 1 &&
                workspace.DocumentCount == 0, "Batch activation retained a document whose binding failed.");
            Console.WriteLine($"Workspace navigation tests passed: {checks}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private sealed class Prompts : IWorkspacePrompts
    {
        public int Reads;
        public bool FailRead;
        public SavePromptResult CloseAnswer = SavePromptResult.Discard;
        public Package ReadPackage(string path, bool isJson)
        {
            Reads++;
            if (FailRead) throw new InvalidDataException("read fixture failure");
            return new Package { Name = "Same.fng", Filename = path };
        }
        public bool WritePackage(WorkspaceDocument document, string path, bool isJson, bool offerOpenFolder) => true;
        public bool SaveContainerProject(bool chooseTarget) => true;
        public string ChooseSaveTarget(WorkspaceDocument document, out bool isJson) { isJson = false; return null; }
        public SavePromptResult AskToSave(WorkspaceDocument document, string reason) => CloseAnswer;
        public bool ConfirmDiscard(WorkspaceDocument document, string reason) => true;
        public void ShowError(string title, string message) { }
    }
}

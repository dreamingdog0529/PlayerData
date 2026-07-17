using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

namespace PlayerData.Unity.Editor.Tests
{
    public sealed class PlayerDataViewerWindowTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlayerDataEditorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        [Test]
        public void Window_OpenAndClose_DoesNotThrow()
        {
            // GetWindow can emit a graphics-device error under -batchmode -nographics; the
            // window lifecycle itself must still complete without an exception.
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            try
            {
                Assert.DoesNotThrow(static () =>
                {
                    PlayerDataViewerWindow window = EditorWindow.GetWindow<PlayerDataViewerWindow>();
                    window.Close();
                });
            }
            finally
            {
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        public void BuildInto_FreshRoot_CreatesNamedElements()
        {
            VisualElement root = new VisualElement();
            PlayerDataViewerController controller = new PlayerDataViewerController();

            ViewerUI.BuildInto(root, controller, _root);

            Assert.That(root.Q<DropdownField>(ViewerUI.SessionDropdownName), Is.Not.Null);
            Assert.That(root.Q<TextField>(ViewerUI.RootPathName), Is.Not.Null);
            Assert.That(root.Q<Button>(ViewerUI.ScanButtonName), Is.Not.Null);
            Assert.That(root.Q<Button>(ViewerUI.ReloadButtonName), Is.Not.Null);
            Assert.That(root.Q<DropdownField>(ViewerUI.SaveDropdownName), Is.Not.Null);
            Assert.That(root.Q<ListView>(ViewerUI.DocumentsListName), Is.Not.Null);
            Assert.That(root.Q<Label>(ViewerUI.DocumentInfoName), Is.Not.Null);
            Assert.That(root.Q<TextField>(ViewerUI.DocumentJsonName), Is.Not.Null);
            Assert.That(root.Q<HelpBox>(ViewerUI.PlayModeWarningName), Is.Not.Null);
            Assert.That(root.Q<TextField>(ViewerUI.RootPathName).value, Is.EqualTo(_root));
            Assert.That(controller.SessionTypes, Has.Member(typeof(SampleEditorSession)));
        }

        [Test]
        public void BuildInto_FreshRoot_CreatesEditControlsDisabledAndReadOnly()
        {
            VisualElement root = new VisualElement();
            PlayerDataViewerController controller = new PlayerDataViewerController();

            ViewerUI.BuildInto(root, controller, _root);

            Button applyButton = root.Q<Button>(ViewerUI.ApplyButtonName);
            Button revertButton = root.Q<Button>(ViewerUI.RevertButtonName);
            HelpBox applyError = root.Q<HelpBox>(ViewerUI.ApplyErrorName);
            TextField json = root.Q<TextField>(ViewerUI.DocumentJsonName);
            Assert.That(applyButton, Is.Not.Null);
            Assert.That(revertButton, Is.Not.Null);
            Assert.That(applyError, Is.Not.Null);
            Assert.That(applyButton.enabledSelf, Is.False, "Apply must start disabled");
            Assert.That(revertButton.enabledSelf, Is.False, "Revert must start disabled");
            Assert.That(json.isReadOnly, Is.True, "JSON field must start read-only");
        }

        [Test]
        public void Controller_FullViewFlow_ShowsJsonForEditableDocument()
        {
            string saveRoot = SampleSaveMenu.Create(_root);
            PlayerDataViewerController controller = new PlayerDataViewerController();
            controller.RefreshSessionTypes();

            controller.SelectSession(typeof(SampleEditorSession));
            controller.Scan(saveRoot);

            Assert.That(controller.Saves, Has.Count.EqualTo(2), "direct save + slot_1 expected");
            Assert.That(controller.Saves[1].Slot, Is.EqualTo(1));

            controller.SelectSave(controller.Saves[0]);
            Assert.That(controller.LoadError, Is.Null);
            Assert.That(controller.CurrentSave, Is.Not.Null);

            DocumentView? profile = controller.GetDocumentView("SampleProfile");
            Assert.That(profile, Is.Not.Null);
            Assert.That(profile.CanEdit, Is.True);
            Assert.That(profile.Json, Does.Contain("hero"));

            DocumentView? mystery = controller.GetDocumentView("mystery");
            Assert.That(mystery, Is.Not.Null);
            Assert.That(mystery.CanEdit, Is.False);
            Assert.That(mystery.Json, Is.Null);

            DocumentView? stats = controller.GetDocumentView("Stats");
            Assert.That(stats, Is.Not.Null);
            Assert.That(stats.Entry.State, Is.EqualTo(DocumentState.Unreadable));
            Assert.That(stats.CanEdit, Is.False);
        }

        [Test]
        public void Controller_ScanWithoutSession_ReportsLoadErrorOnSelect()
        {
            string saveRoot = SampleSaveMenu.Create(_root);
            PlayerDataViewerController controller = new PlayerDataViewerController();
            controller.Scan(saveRoot);

            controller.SelectSave(controller.Saves[0]);

            Assert.That(controller.CurrentSave, Is.Null);
            Assert.That(controller.LoadError, Is.Not.Null);
        }
    }
}

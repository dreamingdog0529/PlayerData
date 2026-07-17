using System;
using System.IO;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace PlayerData.Unity.Editor.Tests
{
    // Reduced with the two-pane rewrite: the Source-dropdown live flow this suite exercised was
    // deleted. Live coverage is rebuilt on top of the "Playing now" tree group in a later step.
    public sealed class PlayerDataViewerLiveModeTests
    {
        [Test]
        public void RegisterLiveSession_WhilePanelBuilt_DoesNotThrow()
        {
            string root = Path.Combine(Path.GetTempPath(), "PlayerDataEditorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            LiveSessionRegistry.ClearForTests();
            SampleEditorSession session = new SampleEditorSession(new DirectorySaveBackend(root));
            try
            {
                ViewerPanel panel = ViewerUI.BuildInto(new VisualElement(), new PlayerDataViewerController(), root);
                Assert.DoesNotThrow(() =>
                {
                    using (LiveSessionRegistry.Register("game", session))
                    {
                    }
                });
                panel.Dispose();
            }
            finally
            {
                LiveSessionRegistry.ClearForTests();
                session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }
    }
}

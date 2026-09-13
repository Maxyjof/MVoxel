namespace MVoxel.Editor;

public static class EditorApplication
{
    public static void Run(string[] args)
    {
        // GLFW requires every native window to be created on the process main
        // thread. Both windows are nevertheless independent OpenTK + ImGui
        // windows; the welcome window is pumped beside the editor loop.
        using var editor = new ImGuiEditorWindow(args);
        ImGuiWelcomeWindow? welcome = null;
        if (!editor.HasStartupProject)
        {
            welcome = new ImGuiWelcomeWindow(editor);
            welcome.InitializeForManualLoop();
            editor.AttachWelcomeWindow(welcome);
        }

        try
        {
            editor.Run();
        }
        finally
        {
            welcome?.Dispose();
        }
    }
}

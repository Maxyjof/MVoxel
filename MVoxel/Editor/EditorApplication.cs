namespace MVoxel.Editor;

public static class EditorApplication
{
    public static void Run(string[] args)
    {
        // GLFW requires all native windows and their ImGui contexts to stay
        // on the process main thread.  ImGuiEditorWindow owns the independent
        // welcome window so it can reopen it after a project is closed and
        // apply the application-modal input blocker consistently.
        using var editor = new ImGuiEditorWindow(args);
        editor.Run();
    }
}

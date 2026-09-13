namespace MVoxel.Editor;

public static class EditorApplication
{
    public static void Run(string[] args)
    {
        // GLFW requires all native windows and their ImGui contexts to stay
        // on the process main thread.  ImGuiEditorWindow owns the independent
        // welcome window so it can reopen it after a project is closed and
        // apply the application-modal input blocker consistently.
        //
        // The voxel GPU pipeline needs OpenGL 4.5 compute; drivers that
        // cannot hand out a 4.5 core context fall back to the 3.3 shell with
        // the legacy CPU preview instead of failing to start at all.
        ImGuiEditorWindow editor;
        try
        {
            editor = ImGuiEditorWindow.Create(args, modernApi: true);
        }
        catch (Exception exception) when (IsContextCreationFailure(exception))
        {
            editor = ImGuiEditorWindow.Create(args, modernApi: false);
        }

        using (editor)
        {
            editor.Run();
        }
    }

    private static bool IsContextCreationFailure(Exception exception) =>
        exception.GetType().Name.Contains("OpenTKException", StringComparison.Ordinal) ||
        exception.GetType().Name.Contains("GraphicsContextException", StringComparison.Ordinal);
}

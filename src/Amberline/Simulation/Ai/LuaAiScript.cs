using LuaChunk = Amberline.Data.LuaChunk;

namespace Amberline.Simulation.Ai;

/// <summary>A loaded AI script: one parsed chunk, shared by every unit that runs it.</summary>
public sealed class LuaAiScript
{
    public LuaAiScript(string name, LuaChunk chunk)
    {
        Name = name;
        Chunk = chunk;
    }

    public string Name { get; }

    /// <summary>The main chunk that defines the script's callbacks as globals.</summary>
    public LuaChunk Chunk { get; }
}

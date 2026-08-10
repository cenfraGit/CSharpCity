using System.Numerics;
using Silk.NET.OpenGL;

namespace CSharpCity.Render;

/// <summary>Minimal compile/link/uniform wrapper around a GLSL program.</summary>
public sealed class Shader : IDisposable
{
    readonly GL _gl;
    readonly Dictionary<string, int> _uniforms = new();
    public uint Handle { get; }

    public Shader(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;
        var vert = Compile(ShaderType.VertexShader, vertexSource);
        var frag = Compile(ShaderType.FragmentShader, fragmentSource);

        Handle = _gl.CreateProgram();
        _gl.AttachShader(Handle, vert);
        _gl.AttachShader(Handle, frag);
        _gl.LinkProgram(Handle);
        _gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
            throw new InvalidOperationException($"Program link failed: {_gl.GetProgramInfoLog(Handle)}");

        _gl.DetachShader(Handle, vert);
        _gl.DetachShader(Handle, frag);
        _gl.DeleteShader(vert);
        _gl.DeleteShader(frag);
    }

    uint Compile(ShaderType type, string source)
    {
        var handle = _gl.CreateShader(type);
        _gl.ShaderSource(handle, source);
        _gl.CompileShader(handle);
        _gl.GetShader(handle, ShaderParameterName.CompileStatus, out int compiled);
        if (compiled == 0)
            throw new InvalidOperationException($"{type} compile failed: {_gl.GetShaderInfoLog(handle)}");
        return handle;
    }

    public void Use() => _gl.UseProgram(Handle);

    int Location(string name)
    {
        if (!_uniforms.TryGetValue(name, out int loc))
            _uniforms[name] = loc = _gl.GetUniformLocation(Handle, name);
        return loc;
    }

    public void SetMatrix(string name, Matrix4x4 value)
    {
        unsafe { _gl.UniformMatrix4(Location(name), 1, false, (float*)&value); }
    }

    public void SetVector2(string name, Vector2 value) => _gl.Uniform2(Location(name), value.X, value.Y);
    public void SetVector3(string name, Vector3 value) => _gl.Uniform3(Location(name), value.X, value.Y, value.Z);
    public void SetFloat(string name, float value) => _gl.Uniform1(Location(name), value);
    public void SetInt(string name, int value) => _gl.Uniform1(Location(name), value);

    public void Dispose() => _gl.DeleteProgram(Handle);
}

// FBXService.cs — FBX inspector HTTP service.
// Single-file: ASP.NET Core endpoints + FBX binary parser (ported from FBXRead.cs).
//
// Local  : http://localhost:5290  (fixed via ASPNETCORE_URLS in dev.ps1 / run.ps1)
// Cloud  : PORT env var injected by Cloud Run
//
// POST /inspect[/{command}[/{target}]][?raw][?verbose]   multipart file=<fbx>
// GET  /health
// GET  /

using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Http.Features;

// =============================================================================
// WEB HOST
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

// Cloud Run injects PORT. Locally, always bind to fixed port 5290 —
// regardless of how the process is launched (ps1, dotnet run, exe directly).
var port = Environment.GetEnvironmentVariable("PORT");
var url  = string.IsNullOrEmpty(port)
    ? "http://localhost:25290"      // local — always fixed
    : $"http://0.0.0.0:{port}";    // Cloud Run
builder.WebHost.UseUrls(url);

builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 512L * 1024 * 1024;
});
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 512L * 1024 * 1024;
});

var app = builder.Build();

// ── probes ────────────────────────────────────────────────────────────────────

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/", () => Results.Ok(new
{
    service  = "FBXService",
    version  = "1.0",
    commands = new[] { "all","list","tree","settings","nodes","node/{name}",
                       "meshes","mesh/{name}","materials","material/{name}",
                       "textures","animations" },
    flags    = new[] { "?raw", "?verbose" }
}));

// ── inspect ───────────────────────────────────────────────────────────────────

app.MapPost("/inspect/{command?}/{target?}", async (HttpContext ctx, string? command, string? target) =>
{
    if (!ctx.Request.HasFormContentType)
        return Results.BadRequest(SimpleError("Request must be multipart/form-data with a 'file' field."));

    IFormFile? upload;
    try   { var form = await ctx.Request.ReadFormAsync(); upload = form.Files["file"]; }
    catch (Exception ex) { return Results.BadRequest(SimpleError($"Failed to read form: {ex.Message}")); }

    if (upload is null || upload.Length == 0)
        return Results.BadRequest(SimpleError("No file uploaded. Send a .fbx file in the 'file' form field."));

    bool   raw     = ctx.Request.Query.ContainsKey("raw");
    bool   verbose = ctx.Request.Query.ContainsKey("verbose");
    string cmd     = (command ?? "all").ToLowerInvariant();

    byte[] data;
    try
    {
        using var ms = new MemoryStream((int)upload.Length);
        await upload.CopyToAsync(ms);
        data = ms.ToArray();
    }
    catch (Exception ex)
    {
        return Results.Json(FbxErrors.Make(FbxErrorType.FileReadError,
            $"Failed to read uploaded file: {ex.Message}", "Upload", null, verbose));
    }

    FbxReadSession session;
    try   { session = FbxReadSession.Parse(upload.FileName ?? "upload.fbx", data); }
    catch (NotSupportedException ex)
    { return Results.Json(FbxErrors.Make(FbxErrorType.ParseError, ex.Message, "FbxParser", ex, verbose), statusCode: 422); }
    catch (InvalidDataException ex)
    { return Results.Json(FbxErrors.Make(FbxErrorType.ParseError, $"FBX parse error: {ex.Message}", "FbxParser", ex, verbose), statusCode: 422); }
    catch (Exception ex)
    { return Results.Json(FbxErrors.Make(FbxErrorType.InternalError, $"Parser crash: {ex.Message}", "FbxParser", ex, verbose), statusCode: 500); }

    object? result;
    try
    {
        result = FBXReadCommands.Dispatch(cmd, session, target, raw, verbose);
        if (result is null)
            return Results.Json(FbxErrors.Make(FbxErrorType.BadArguments,
                $"Unknown command '{cmd}'. Valid: all list tree settings nodes node meshes mesh materials material textures animations",
                "Dispatch", null, verbose), statusCode: 400);
    }
    catch (Exception ex)
    {
        return Results.Json(FbxErrors.Make(FbxErrorType.CommandError,
            $"Command '{cmd}' threw: {ex.Message}", $"Commands.{cmd}", ex, verbose), statusCode: 500);
    }

    return Results.Json(result);
});

app.Run();

static object SimpleError(string msg) => new Dictionary<string, object>
    { ["success"] = false, ["error"] = msg, ["errorType"] = "BadRequest" };

// =============================================================================
// ERROR TYPES
// =============================================================================

internal enum FbxErrorType
{
    FileNotFound, FileReadError, BadArguments, ParseError,
    ExtractWarning, CommandError, InternalError
}

internal static class FbxErrors
{
    public static Dictionary<string, object?> Make(
        FbxErrorType type, string message, string stage, Exception? ex, bool verbose)
    {
        var d = new Dictionary<string, object?>
        {
            ["success"]         = false,
            ["error"]           = message,
            ["errorType"]       = type.ToString(),
            ["stage"]           = stage,
            ["rebuildRequired"] = type is FbxErrorType.CommandError or FbxErrorType.InternalError
        };
        if (ex is not null)
        {
            d["hint"] = $"Caused by {ex.GetType().Name}: {ex.Message}";
            if (verbose)
            {
                d["exceptionType"] = ex.GetType().FullName;
                d["stackTrace"]    = ex.StackTrace ?? "(no stack trace)";
                if (ex.InnerException is not null)
                {
                    d["innerException"]     = ex.InnerException.Message;
                    d["innerExceptionType"] = ex.InnerException.GetType().FullName;
                    d["innerStackTrace"]    = ex.InnerException.StackTrace ?? "";
                }
            }
            else { d["tip"] = "Add ?verbose to the request URL to see the full stack trace."; }
        }
        return d;
    }

    public static object NotFound(string kind, string name, IEnumerable<string> available) =>
        new Dictionary<string, object>
        {
            ["success"]   = false,  ["error"] = $"{kind} '{name}' not found.",
            ["errorType"] = FbxErrorType.BadArguments.ToString(),
            ["stage"]     = $"Command_{kind}",
            ["hint"]      = "Check the name against the 'available' list below.",
            ["available"] = available.ToList<object>()
        };

    public static object MissingTarget(string command) =>
        new Dictionary<string, object>
        {
            ["success"]   = false,  ["error"] = $"'{command}' requires a name argument.",
            ["errorType"] = FbxErrorType.BadArguments.ToString(),
            ["stage"]     = $"Command_{command}",
            ["hint"]      = $"Usage: POST /inspect/{command}/{{name}}"
        };
}

// =============================================================================
// VECTOR / COLOR STUBS  (replacing UnityEngine types)
// =============================================================================

internal record struct Vec2(float X, float Y)
{
    public static readonly Vec2 Zero = new(0, 0);
    public static readonly Vec2 One  = new(1, 1);
}
internal record struct Vec3(float X, float Y, float Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);
    public static readonly Vec3 One  = new(1, 1, 1);
}
internal record struct Col(float R, float G, float B, float A)
{
    public static readonly Col White = new(1, 1, 1, 1);
    public static readonly Col Black = new(0, 0, 0, 1);
}

// =============================================================================
// SESSION
// =============================================================================

internal class FbxReadSession
{
    public string       FilePath = "", FileName = "";
    public long         FileSize;
    public uint         FbxVersion;
    public FbxDocument  Doc = null!;
    public FbxSceneData Scene = null!;
    public List<string> Warnings = [];

    public static FbxReadSession Parse(string filePath, byte[] data)
    {
        var s = new FbxReadSession
            { FilePath = filePath, FileName = Path.GetFileName(filePath), FileSize = data.Length };
        FbxDocument doc;
        using (var ms = new MemoryStream(data))
        using (var br = new BinaryReader(ms, Encoding.ASCII, leaveOpen: false))
            doc = FbxParser.Parse(br);
        s.Doc        = doc;
        s.FbxVersion = doc.Version;
        s.Scene      = FbxSceneExtractor.Extract(doc, s.Warnings);
        return s;
    }
}

// =============================================================================
// COMMANDS
// =============================================================================

internal static class FBXReadCommands
{
    public static object? Dispatch(string cmd, FbxReadSession s, string? target, bool raw, bool verbose)
        => cmd switch
        {
            "all"        => All(s, raw),
            "list"       => List(s),
            "tree"       => Tree(s),
            "settings"   => Settings(s),
            "nodes"      => Nodes(s),
            "node"       => Node(s, target, verbose),
            "meshes"     => Meshes(s, raw),
            "mesh"       => Mesh(s, target, raw, verbose),
            "materials"  => Materials(s),
            "material"   => Material(s, target, verbose),
            "textures"   => Textures(s),
            "animations" => Animations(s),
            _            => null
        };

    static object All(FbxReadSession s, bool raw) => D(
        "success",    true,
        "file",       s.FileName,        "filePath",   s.FilePath,
        "fileSize",   s.FileSize,        "fbxVersion", s.FbxVersion,
        "warnings",   s.Warnings.Cast<object>().ToList(),
        "stats",      StatsObj(s),       "settings",   SettingsObj(s.Scene.Settings),
        "nodes",      NodesArr(s),       "meshes",     MeshesArr(s, raw),
        "materials",  MaterialsArr(s),   "textures",   TexturesArr(s)
    );

    static object List(FbxReadSession s) => D(
        "file", s.FileName, "fbxVersion", s.FbxVersion,
        "categories", new List<object>
        {
            D("name","settings",   "description","Coordinate system, scale, frame-rate"),
            D("name","nodes",      "count",s.Scene.Models.Count, "description","Transform hierarchy"),
            D("name","meshes",     "count",s.Scene.Geoms.Count,  "description","Geometry / mesh data"),
            D("name","materials",  "count",s.Scene.Mats.Count,   "description","Materials"),
            D("name","textures",   "count",s.Scene.Texs.Count,   "description","Texture references"),
            D("name","animations", "count",AnimCount(s),          "description","Animation stacks (not extracted)"),
            D("name","tree",       "description","Raw FBX node-tree structure")
        }
    );

    static int AnimCount(FbxReadSession s) =>
        s.Doc.FindRoot("Objects")?.Children.Count(n => n.Name == "AnimationStack") ?? 0;

    static object Tree(FbxReadSession s) => D(
        "file", s.FileName, "fbxVersion", s.FbxVersion,
        "rootNodes", s.Doc.Roots.Select(n => TreeNode(n, 0)).ToList<object>()
    );

    static object TreeNode(FbxNode n, int depth)
    {
        var props = n.Props.Select(p =>
        {
            if (p.Value is Array a && a.Length > 12) return (object)$"[{p.Code}[{a.Length}]]";
            if (p.Value is byte[] b && b.Length > 12) return (object)$"[R[{b.Length}]]";
            if (p.Value is string sv) return (object)sv;
            return p.Value;
        }).ToList<object>();
        var d = (Dictionary<string, object?>)D("name", n.Name, "propCount", n.Props.Count, "props", props);
        if (n.Children.Count > 0 && depth < 6)
            d["children"] = n.Children.Select(c => TreeNode(c, depth + 1)).ToList<object>();
        else if (n.Children.Count > 0)
            d["children"] = $"[{n.Children.Count} children — depth limit reached]";
        return d;
    }

    static object Settings(FbxReadSession s) => SettingsObj(s.Scene.Settings);
    static object SettingsObj(GlobalSettings gs)
    {
        string desc = gs.UpAxis == 2 && gs.FrontAxis == 1
            ? $"Z-up right-handed (Blender) — unitScale={gs.UnitScaleFactor}"
            : gs.UpAxis == 1 && gs.FrontAxis == 2
                ? $"Y-up right-handed (Maya/Max) — unitScale={gs.UnitScaleFactor}"
                : $"{Ax(gs.UpAxis)}-up, {Ax(gs.FrontAxis)}-front — unitScale={gs.UnitScaleFactor}";
        return D(
            "upAxis",gs.UpAxis,          "upAxisSign",gs.UpAxisSign,
            "frontAxis",gs.FrontAxis,    "frontAxisSign",gs.FrontAxisSign,
            "coordAxis",gs.CoordAxis,    "coordAxisSign",gs.CoordAxisSign,
            "unitScaleFactor",gs.UnitScaleFactor, "frameRate",gs.FrameRate,
            "coordinateSystem",desc
        );
    }
    static string Ax(int a) => a == 0 ? "X" : a == 1 ? "Y" : "Z";

    static object Nodes(FbxReadSession s) => D("nodes", NodesArr(s));
    static List<object> NodesArr(FbxReadSession s) =>
        s.Scene.Models.Values.Select(NodeObj).ToList<object>();

    static object Node(FbxReadSession s, string? name, bool verbose)
    {
        if (string.IsNullOrEmpty(name)) return FbxErrors.MissingTarget("node");
        var m = s.Scene.Models.Values.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return m is null ? FbxErrors.NotFound("Node", name, s.Scene.Models.Values.Select(x => x.Name)) : NodeObj(m);
    }

    static object NodeObj(SceneModel m) => D(
        "id",m.Id,                  "name",m.Name,          "subclass",m.SubClass, "parentId",m.ParentId,
        "translation",V3(m.LclT),  "rotation",V3(m.LclR), "scale",V3(m.LclS),
        "preRotation",V3(m.PreRotation),  "postRotation",V3(m.PostRotation),
        "rotationPivot",V3(m.RotationPivot), "scalingPivot",V3(m.ScalingPivot),
        "rotationOffset",V3(m.RotationOffset), "scalingOffset",V3(m.ScalingOffset),
        "geometryIds",m.Geometries.Cast<object>().ToList(),
        "materialIds",m.MaterialIds.Cast<object>().ToList()
    );

    static object Meshes(FbxReadSession s, bool raw) => D("meshes", MeshesArr(s, raw));
    static List<object> MeshesArr(FbxReadSession s, bool raw) =>
        s.Scene.Geoms.Values.Select(g => MeshObj(g, raw)).ToList<object>();

    static object Mesh(FbxReadSession s, string? name, bool raw, bool verbose)
    {
        if (string.IsNullOrEmpty(name)) return FbxErrors.MissingTarget("mesh");
        var g = s.Scene.Geoms.Values.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return g is null ? FbxErrors.NotFound("Mesh", name, s.Scene.Geoms.Values.Select(x => x.Name)) : MeshObj(g, raw);
    }

    static object MeshObj(SceneGeometry g, bool raw)
    {
        int cpCount = g.RawVertices?.Length / 3 ?? 0;
        int pvCount = g.PolyVertexIndex?.Length ?? 0;
        int polyCount = 0;
        if (g.PolyVertexIndex is not null) foreach (int idx in g.PolyVertexIndex) if (idx < 0) polyCount++;

        var uvInfos = g.UVLayers.Select(u => D(
            "index",u.Index, "name",u.Name ?? $"UV{u.Index}",
            "elementCount",u.UVRaw?.Length / 2 ?? 0, "mapping",u.Mapping, "reference",u.Ref
        )).ToList<object>();

        var d = (Dictionary<string, object?>)D(
            "id",g.Id, "name",g.Name,
            "controlPointCount",cpCount, "polygonCount",polyCount, "polygonVertexCount",pvCount,
            "uvLayers",uvInfos,
            "hasNormals",g.NormalsRaw is not null, "normalsMapping",g.NormalsMapping, "normalsReference",g.NormalsRef,
            "normalsElementCount",g.NormalsRaw?.Length / 3 ?? 0,
            "hasTangents",g.TangentsRaw is not null, "tangentElementCount",g.TangentsRaw?.Length / 3 ?? 0,
            "hasVertexColors",g.ColorsRaw is not null, "colorElementCount",g.ColorsRaw?.Length / 4 ?? 0,
            "materialMapping",g.MaterialMapping, "materialIndexCount",g.MaterialIndices?.Length ?? 0
        );
        if (raw) AppendRaw(d, g);
        return d;
    }

    static void AppendRaw(Dictionary<string, object?> d, SceneGeometry g)
    {
        if (g.RawVertices is not null)
        {
            int n = g.RawVertices.Length / 3;
            var v = new List<object>(n);
            for (int i = 0; i < n; i++)
                v.Add(new List<object> { g.RawVertices[i*3], g.RawVertices[i*3+1], g.RawVertices[i*3+2] });
            d["vertices"] = v;
        }
        if (g.PolyVertexIndex is not null)
            d["polygonVertexIndex"] = g.PolyVertexIndex.Cast<object>().ToList();
        if (g.NormalsRaw is not null)
        {
            int n = g.NormalsRaw.Length / 3;
            var nrm = new List<object>(n);
            for (int i = 0; i < n; i++)
                nrm.Add(new List<object> { g.NormalsRaw[i*3], g.NormalsRaw[i*3+1], g.NormalsRaw[i*3+2] });
            d["normals"] = nrm;
            if (g.NormalsIndex is not null) d["normalsIndex"] = g.NormalsIndex.Cast<object>().ToList();
        }
        if (g.UVLayers.Count > 0 && g.UVLayers[0].UVRaw is not null)
        {
            var u0 = g.UVLayers[0]; int n = u0.UVRaw!.Length / 2;
            var uvs = new List<object>(n);
            for (int i = 0; i < n; i++) uvs.Add(new List<object> { u0.UVRaw[i*2], u0.UVRaw[i*2+1] });
            d["uv0"] = uvs;
            if (u0.UVIndex is not null) d["uv0Index"] = u0.UVIndex.Cast<object>().ToList();
        }
        if (g.MaterialIndices is not null)
            d["materialIndices"] = g.MaterialIndices.Cast<object>().ToList();
    }

    static object Materials(FbxReadSession s) => D("materials", MaterialsArr(s));
    static List<object> MaterialsArr(FbxReadSession s) =>
        s.Scene.Mats.Values.Select(MatObj).ToList<object>();

    static object Material(FbxReadSession s, string? name, bool verbose)
    {
        if (string.IsNullOrEmpty(name)) return FbxErrors.MissingTarget("material");
        var m = s.Scene.Mats.Values.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return m is null ? FbxErrors.NotFound("Material", name, s.Scene.Mats.Values.Select(x => x.Name)) : MatObj(m);
    }

    static object MatObj(SceneMaterial m) => D(
        "id",m.Id, "name",m.Name, "shadingModel",m.ShadingModel,
        "diffuseColor",CA(m.DiffuseColor), "emissiveColor",CA(m.EmissiveColor), "specularColor",CA(m.SpecularColor),
        "opacity",m.Opacity, "shininess",m.Shininess, "metallic",m.Metallic, "roughness",m.Roughness,
        "textureSlots",m.TextureSlots.ToDictionary(kv => kv.Key, kv => (object)kv.Value)
    );

    static object Textures(FbxReadSession s) => D("textures", TexturesArr(s));
    static List<object> TexturesArr(FbxReadSession s) =>
        s.Scene.Texs.Values.Select(t => TexObj(t, s)).ToList<object>();

    static object TexObj(SceneTexture t, FbxReadSession s)
    {
        s.Scene.Videos.TryGetValue(t.VideoId, out var vid);
        bool emb = t.VideoId != 0 && vid?.Content is { Length: > 0 };
        return D(
            "id",t.Id, "name",t.Name,
            "relativeFilename",t.RelativeFilename ?? "", "absoluteFilename",t.AbsoluteFilename ?? "",
            "uvTranslation",new List<object>{(double)t.UVTranslation.X,(double)t.UVTranslation.Y},
            "uvScaling",    new List<object>{(double)t.UVScaling.X,    (double)t.UVScaling.Y},
            "videoId",t.VideoId, "hasEmbeddedData",emb, "embeddedByteSize",emb ? vid!.Content!.Length : 0
        );
    }

    static object Animations(FbxReadSession s) => D(
        "supported",false,
        "note","Animation parsing is not yet implemented. Use the 'tree' command to see raw nodes.",
        "animationStackCount",AnimCount(s)
    );

    // ── helpers ───────────────────────────────────────────────────────────────

    static object StatsObj(FbxReadSession s) => D(
        "nodeCount",s.Scene.Models.Count, "geometryCount",s.Scene.Geoms.Count,
        "materialCount",s.Scene.Mats.Count, "textureCount",s.Scene.Texs.Count,
        "videoCount",s.Scene.Videos.Count, "warningCount",s.Warnings.Count
    );

    static List<object> V3(Vec3 v) => [(object)(double)v.X, (double)v.Y, (double)v.Z];
    static List<object> CA(Col c)  => [(object)(double)c.R, (double)c.G, (double)c.B, (double)c.A];

    static Dictionary<string, object?> D(params object?[] kv)
    {
        var d = new Dictionary<string, object?>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]!] = kv[i + 1];
        return d;
    }
}

// =============================================================================
// FBX PARSER  — binary FBX v7100–v7700, pure C#
// =============================================================================

internal sealed class FbxDocument
{
    public uint Version;
    public List<FbxNode> Roots = [];
    public FbxNode? FindRoot(string name) => Roots.FirstOrDefault(n => n.Name == name);
}

internal sealed class FbxNode
{
    public string Name = "";
    public List<FbxProperty> Props = [];
    public List<FbxNode>     Children = [];
    public FbxNode?             Child(string n) => Children.FirstOrDefault(c => c.Name == n);
    public IEnumerable<FbxNode> All(string n)   => Children.Where(c => c.Name == n);
    public string PropString(int i) => i < Props.Count ? Props[i].Value as string ?? "" : "";
    public long   PropLong(int i)   => i < Props.Count ? Convert.ToInt64(Props[i].Value) : 0L;
}

internal sealed class FbxProperty { public char Code; public object? Value; }

internal static class FbxParser
{
    public static FbxDocument Parse(BinaryReader br)
    {
        var doc = new FbxDocument();
        ReadHeader(br, out doc.Version);
        bool wide = doc.Version >= 7500;
        while (br.BaseStream.Position < br.BaseStream.Length)
        {
            var n = ReadNode(br, wide);
            if (n is null) break;
            doc.Roots.Add(n);
        }
        return doc;
    }

    static void ReadHeader(BinaryReader br, out uint version)
    {
        long   start = br.BaseStream.Position;
        byte[] buf   = br.ReadBytes(64);
        if (buf.Length < 23) throw new InvalidDataException("FBX header too short.");
        int ofs = (buf.Length >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) ? 3 : 0;
        string ascii = Encoding.ASCII.GetString(buf, ofs, Math.Min(32, buf.Length - ofs));
        if (ascii.StartsWith("Kaydara FBX ASCII"))
            throw new NotSupportedException("ASCII FBX not supported. Re-export as Binary FBX from your DCC tool.");
        const string magic = "Kaydara FBX Binary";
        for (int i = 0; i < magic.Length; i++)
            if (ofs + i >= buf.Length || buf[ofs + i] != magic[i])
                throw new InvalidDataException($"Not a binary FBX file (magic mismatch at byte {ofs + i}).");
        int p = ofs + magic.Length;
        while (p < buf.Length && buf[p] == (byte)' ') p++;
        if (!(p + 2 < buf.Length && buf[p] == 0x00 && buf[p + 1] == 0x1A && buf[p + 2] == 0x00))
            throw new InvalidDataException($"FBX header markers invalid at offset {p}.");
        br.BaseStream.Position = start + (p + 3);
        version = br.ReadUInt32();
    }

    static FbxNode? ReadNode(BinaryReader br, bool wide)
    {
        long pos = br.BaseStream.Position;
        long endOffset, numProps, propsLen;
        if (wide)
        { endOffset=(long)br.ReadUInt64(); numProps=(long)br.ReadUInt64(); propsLen=(long)br.ReadUInt64(); }
        else
        { endOffset=br.ReadUInt32(); numProps=br.ReadUInt32(); propsLen=br.ReadUInt32(); }
        byte nameLen = br.ReadByte();
        if (endOffset == 0 && numProps == 0 && propsLen == 0 && nameLen == 0) return null;
        string name = nameLen > 0 ? Encoding.ASCII.GetString(br.ReadBytes(nameLen)) : "";
        var node = new FbxNode { Name = name };
        try
        {
            for (long i = 0; i < numProps; i++) node.Props.Add(ReadProp(br));
            while (br.BaseStream.Position < endOffset)
            {
                var child = ReadNode(br, wide);
                if (child is null) break;
                node.Children.Add(child);
            }
        }
        catch (Exception ex) when (ex is not NotSupportedException)
        {
            throw new InvalidDataException(
                $"Parse failure in node '{name}' at offset {pos}: {ex.Message}", ex);
        }
        br.BaseStream.Position = endOffset;
        return node;
    }

    static FbxProperty ReadProp(BinaryReader br)
    {
        long pos  = br.BaseStream.Position;
        char code = (char)br.ReadByte();
        object? val;
        try
        {
            val = code switch
            {
                'Y' => br.ReadInt16(),
                'C' => br.ReadByte() != 0,
                'I' => br.ReadInt32(),
                'F' => br.ReadSingle(),
                'D' => br.ReadDouble(),
                'L' => br.ReadInt64(),
                'S' => Encoding.UTF8.GetString(br.ReadBytes((int)br.ReadUInt32())),
                'R' => br.ReadBytes((int)br.ReadUInt32()),
                'f' => RA(br, 4, r => (object)r.ReadSingle()),
                'd' => RA(br, 8, r => (object)r.ReadDouble()),
                'i' => RA(br, 4, r => (object)r.ReadInt32()),
                'l' => RA(br, 8, r => (object)r.ReadInt64()),
                'b' => RA(br, 1, r => (object)(r.ReadByte() != 0)),
                _   => throw new NotSupportedException(
                           $"Unsupported FBX property type '{code}' (0x{(int)code:X2}) at offset {pos}.")
            };
        }
        catch (EndOfStreamException ex)
        { throw new InvalidDataException($"Unexpected EOF at property '{code}' offset {pos}.", ex); }
        return new FbxProperty { Code = code, Value = val };
    }

    static T[] RA<T>(BinaryReader br, int elem, Func<BinaryReader, T> read)
    {
        uint count = br.ReadUInt32(), enc = br.ReadUInt32(), clen = br.ReadUInt32();
        if (enc == 0) { var a = new T[count]; for (int i = 0; i < count; i++) a[i] = read(br); return a; }
        if (enc != 1) throw new InvalidDataException($"Unknown FBX array encoding: {enc}");
        byte[] comp     = br.ReadBytes((int)clen);
        int    expected = checked((int)(count * (uint)elem));
        byte[] raw      = Zlib(comp, expected);
        using var ms = new MemoryStream(raw); using var rr = new BinaryReader(ms);
        var arr = new T[count]; for (int i = 0; i < count; i++) arr[i] = read(rr); return arr;
    }

    static byte[] Zlib(byte[] comp, int expected)
    {
        try
        {
            using var cms = new MemoryStream(comp);
            using var ds  = new DeflateStream(cms, CompressionMode.Decompress);
            using var ms  = new MemoryStream();
            ds.CopyTo(ms); var b = ms.ToArray();
            if (b.Length >= expected) { Array.Resize(ref b, expected); return b; }
        }
        catch { }
        int skip = 0, trim = 0;
        if (comp.Length >= 6 && comp[0] == 0x78) { skip = 2; trim = 4; }
        int rawLen = Math.Max(0, comp.Length - skip - trim);
        if (rawLen <= 0) throw new InvalidDataException("Zlib stream too short.");
        using var cms2 = new MemoryStream(comp, skip, rawLen);
        using var ds2  = new DeflateStream(cms2, CompressionMode.Decompress);
        using var ms2  = new MemoryStream();
        ds2.CopyTo(ms2); var b2 = ms2.ToArray();
        if (b2.Length > expected) Array.Resize(ref b2, expected);
        return b2;
    }
}

// =============================================================================
// SCENE DATA MODEL
// =============================================================================

internal sealed class FbxSceneData
{
    public Dictionary<long, SceneModel>    Models = [];
    public Dictionary<long, SceneGeometry> Geoms  = [];
    public Dictionary<long, SceneMaterial> Mats   = [];
    public Dictionary<long, SceneTexture>  Texs   = [];
    public Dictionary<long, SceneVideo>    Videos = [];
    public List<long>      RootIds  = [];
    public GlobalSettings  Settings = new();
}

internal sealed class GlobalSettings
{
    public int   UpAxis=1, UpAxisSign=1, FrontAxis=2, FrontAxisSign=1;
    public int   CoordAxis=0, CoordAxisSign=1;
    public float UnitScaleFactor=1f, FrameRate=24f;
}

internal sealed class SceneModel
{
    public long Id; public string Name="", SubClass="";
    public List<long> Geometries=[], MaterialIds=[];
    public long ParentId;
    public Vec3 LclT, LclR, LclS=Vec3.One;
    public Vec3 PreRotation, PostRotation, RotationPivot, ScalingPivot, RotationOffset, ScalingOffset;
}

internal sealed class SceneGeometry
{
    public long Id; public string Name="";
    public double[]? RawVertices, NormalsRaw, TangentsRaw, ColorsRaw;
    public int[]?    PolyVertexIndex, NormalsIndex, TangentsIndex, ColorsIndex, MaterialIndices;
    public string    NormalsMapping="ByPolygonVertex",  NormalsRef="Direct";
    public string    TangentsMapping="ByPolygonVertex", TangentsRef="Direct";
    public string    ColorsMapping="ByPolygonVertex",   ColorsRef="Direct";
    public string    MaterialMapping="ByPolygon";
    public List<UVLayer> UVLayers=[];
}

internal sealed class UVLayer
{
    public int Index; public string? Name;
    public double[]? UVRaw; public int[]? UVIndex;
    public string Mapping="ByPolygonVertex", Ref="IndexToDirect";
}

internal sealed class SceneMaterial
{
    public long Id; public string Name="", ShadingModel="Phong";
    public Col DiffuseColor=Col.White, EmissiveColor=Col.Black, SpecularColor=Col.White;
    public float Opacity=1f, Shininess=20f, Metallic=0f, Roughness=0.5f;
    public Dictionary<string,long> TextureSlots=[];
}

internal sealed class SceneTexture
{
    public long Id; public string Name="";
    public string? RelativeFilename, AbsoluteFilename;
    public Vec2 UVTranslation, UVScaling=Vec2.One;
    public long VideoId;
}

internal sealed class SceneVideo
{
    public long Id; public string Name=""; public byte[]? Content;
}

// =============================================================================
// SCENE EXTRACTOR
// =============================================================================

internal static class FbxSceneExtractor
{
    public static FbxSceneData Extract(FbxDocument doc, List<string> warnings)
    {
        var scene = new FbxSceneData();
        var gs = doc.FindRoot("GlobalSettings");
        if (gs is not null) ReadGS(gs, scene.Settings);
        var objects = doc.FindRoot("Objects");
        if (objects is null)
        { warnings.Add("[ERROR] FBX missing 'Objects' node — file may be corrupt."); return scene; }
        foreach (var node in objects.Children)
        {
            try
            {
                switch (node.Name)
                {
                    case "Model":    ParseModel(node, scene); break;
                    case "Geometry": ParseGeom(node, scene);  break;
                    case "Material": ParseMat(node, scene);   break;
                    case "Texture":  ParseTex(node, scene);   break;
                    case "Video":    ParseVid(node, scene);   break;
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"[WARN] Skipped {node.Name} '{node.PropString(1)}' " +
                             $"(id={node.PropLong(0)}): {ex.GetType().Name}: {ex.Message}");
            }
        }
        var conns = doc.FindRoot("Connections");
        if (conns is not null) ProcessConns(conns, scene);
        foreach (var kv in scene.Models)
            if (kv.Value.ParentId == 0 || !scene.Models.ContainsKey(kv.Value.ParentId))
                scene.RootIds.Add(kv.Key);
        return scene;
    }

    static void ReadGS(FbxNode gs, GlobalSettings s)
    {
        var p70 = gs.Child("Properties70"); if (p70 is null) return;
        foreach (var p in p70.All("P"))
            switch (p.PropString(0))
            {
                case "UpAxis":          s.UpAxis          = PI(p,4); break;
                case "UpAxisSign":      s.UpAxisSign      = PI(p,4); break;
                case "FrontAxis":       s.FrontAxis       = PI(p,4); break;
                case "FrontAxisSign":   s.FrontAxisSign   = PI(p,4); break;
                case "CoordAxis":       s.CoordAxis       = PI(p,4); break;
                case "CoordAxisSign":   s.CoordAxisSign   = PI(p,4); break;
                case "UnitScaleFactor": s.UnitScaleFactor = PF(p,4); break;
                case "CustomFrameRate": s.FrameRate       = PF(p,4); break;
            }
    }

    static void ParseModel(FbxNode node, FbxSceneData scene)
    {
        var m = new SceneModel { Id=node.PropLong(0), Name=Clean(node.PropString(1)), SubClass=node.PropString(2) };
        var p70 = node.Child("Properties70");
        if (p70 is not null) foreach (var p in p70.All("P"))
            switch (p.PropString(0))
            {
                case "Lcl Translation": m.LclT           = PV3(p); break;
                case "Lcl Rotation":    m.LclR           = PV3(p); break;
                case "Lcl Scaling":     m.LclS           = PV3(p); break;
                case "PreRotation":     m.PreRotation    = PV3(p); break;
                case "PostRotation":    m.PostRotation   = PV3(p); break;
                case "RotationPivot":   m.RotationPivot  = PV3(p); break;
                case "ScalingPivot":    m.ScalingPivot   = PV3(p); break;
                case "RotationOffset":  m.RotationOffset = PV3(p); break;
                case "ScalingOffset":   m.ScalingOffset  = PV3(p); break;
            }
        scene.Models[m.Id] = m;
    }

    static void ParseGeom(FbxNode node, FbxSceneData scene)
    {
        var g = new SceneGeometry { Id=node.PropLong(0), Name=Clean(node.PropString(1)) };
        var vn  = node.Child("Vertices");         if (vn?.Props.Count  > 0 && vn.Props[0].Value  is double[] vd)  g.RawVertices    = vd;
        var pvi = node.Child("PolygonVertexIndex");if (pvi?.Props.Count > 0 && pvi.Props[0].Value is int[]    idx) g.PolyVertexIndex = idx;
        var ln  = node.All("LayerElementNormal").FirstOrDefault();
        if (ln is not null) LE(ln,"Normals","NormalsIndex", out g.NormalsRaw, out g.NormalsIndex, out g.NormalsMapping, out g.NormalsRef);
        var lt = node.All("LayerElementTangent").FirstOrDefault();
        if (lt is not null) LE(lt,"Tangents","TangentsIndex", out g.TangentsRaw, out g.TangentsIndex, out g.TangentsMapping, out g.TangentsRef);
        int uvIdx = 0;
        foreach (var luv in node.All("LayerElementUV"))
        {
            var layer = new UVLayer { Index=uvIdx++, Name=luv.Child("Name")?.PropString(0) };
            LE(luv,"UV","UVIndex", out layer.UVRaw, out layer.UVIndex, out layer.Mapping, out layer.Ref);
            if (layer.UVRaw is not null) g.UVLayers.Add(layer);
        }
        var lc = node.All("LayerElementColor").FirstOrDefault();
        if (lc is not null) LE(lc,"Colors","ColorIndex", out g.ColorsRaw, out g.ColorsIndex, out g.ColorsMapping, out g.ColorsRef);
        var lm = node.All("LayerElementMaterial").FirstOrDefault();
        if (lm is not null)
        {
            var map = lm.Child("MappingInformationType"); if (map is not null) g.MaterialMapping = map.PropString(0);
            var md  = lm.Child("Materials");
            if (md?.Props.Count > 0 && md.Props[0].Value is int[] ma) g.MaterialIndices = ma;
        }
        scene.Geoms[g.Id] = g;
    }

    static void ParseMat(FbxNode node, FbxSceneData scene)
    {
        var m = new SceneMaterial { Id=node.PropLong(0), Name=Clean(node.PropString(1)) };
        var sm = node.Child("ShadingModel"); if (sm is not null) m.ShadingModel = sm.PropString(0);
        var p70 = node.Child("Properties70");
        if (p70 is not null) foreach (var p in p70.All("P"))
            switch (p.PropString(0))
            {
                case "DiffuseColor":       m.DiffuseColor  = PC(p);         break;
                case "EmissiveColor":       m.EmissiveColor = PC(p);         break;
                case "SpecularColor":       m.SpecularColor = PC(p);         break;
                case "Opacity":            m.Opacity       = PF(p,4);       break;
                case "Shininess":
                case "ShininessExponent":  m.Shininess     = PF(p,4);       break;
                case "Metallic":           m.Metallic      = PF(p,4);       break;
                case "Roughness":          m.Roughness     = PF(p,4);       break;
                case "TransparencyFactor": m.Opacity       = 1f - PF(p,4); break;
            }
        scene.Mats[m.Id] = m;
    }

    static void ParseTex(FbxNode node, FbxSceneData scene)
    {
        var t = new SceneTexture { Id=node.PropLong(0), Name=Clean(node.PropString(1)) };
        var rel = node.Child("RelativeFilename"); if (rel is not null) t.RelativeFilename = rel.PropString(0);
        var abs = node.Child("FileName");         if (abs is not null) t.AbsoluteFilename = abs.PropString(0);
        var p70 = node.Child("Properties70");
        if (p70 is not null) foreach (var p in p70.All("P"))
        {
            if (p.PropString(0) == "Translation") t.UVTranslation = new Vec2(PF(p,4), PF(p,5));
            if (p.PropString(0) == "Scaling")     t.UVScaling     = new Vec2(PF(p,4), PF(p,5));
        }
        var content = node.Child("Content");
        if (content?.Props.Count > 0 && content.Props[0].Value is byte[] bytes && bytes.Length > 4)
        {
            var vid = new SceneVideo { Id=t.Id+100000000L, Name=t.Name, Content=bytes };
            scene.Videos[vid.Id] = vid; t.VideoId = vid.Id;
        }
        scene.Texs[t.Id] = t;
    }

    static void ParseVid(FbxNode node, FbxSceneData scene)
    {
        var v = new SceneVideo { Id=node.PropLong(0), Name=Clean(node.PropString(1)) };
        var content = node.Child("Content");
        if (content?.Props.Count > 0 && content.Props[0].Value is byte[] bytes && bytes.Length > 4)
            v.Content = bytes;
        scene.Videos[v.Id] = v;
    }

    static void ProcessConns(FbxNode conns, FbxSceneData scene)
    {
        foreach (var c in conns.All("C"))
        {
            if (c.Props.Count < 3) continue;
            string mode = c.PropString(0); long cId = c.PropLong(1), pId = c.PropLong(2);
            if (mode == "OO")
            {
                if (scene.Models.TryGetValue(cId, out var cm) && scene.Models.ContainsKey(pId)) cm.ParentId = pId;
                if (scene.Geoms.ContainsKey(cId)  && scene.Models.TryGetValue(pId, out var go)) go.Geometries.Add(cId);
                if (scene.Mats.ContainsKey(cId)   && scene.Models.TryGetValue(pId, out var mo)) mo.MaterialIds.Add(cId);
                if (scene.Videos.ContainsKey(cId) && scene.Texs.TryGetValue(pId, out var to))   to.VideoId = cId;
                if (scene.Texs.ContainsKey(cId)   && scene.Mats.TryGetValue(pId, out var mt))
                    if (!mt.TextureSlots.ContainsKey("DiffuseColor")) mt.TextureSlots["DiffuseColor"] = cId;
            }
            else if (mode == "OP")
            {
                string prop = c.Props.Count > 3 ? c.PropString(3) : "";
                if (scene.Texs.ContainsKey(cId) && scene.Mats.TryGetValue(pId, out var mat))
                    mat.TextureSlots[prop] = cId;
            }
        }
    }

    static void LE(FbxNode layer, string dn, string iName,
        out double[]? data, out int[]? index, out string mapping, out string refType)
    {
        data=null; index=null; mapping="ByPolygonVertex"; refType="Direct";
        var m  = layer.Child("MappingInformationType");   if (m  is not null) mapping  = m.PropString(0);
        var r  = layer.Child("ReferenceInformationType"); if (r  is not null) refType  = r.PropString(0);
        var d  = layer.Child(dn);    if (d?.Props.Count  > 0 && d.Props[0].Value  is double[] da) data  = da;
        var ix = layer.Child(iName); if (ix?.Props.Count > 0 && ix.Props[0].Value is int[]    ia) index = ia;
    }

    static Vec3  PV3(FbxNode p) => p.Props.Count >= 7 ? new Vec3(PF(p,4), PF(p,5), PF(p,6)) : Vec3.Zero;
    static Col   PC(FbxNode p)  { var v = PV3(p); return new Col(v.X, v.Y, v.Z, 1f); }
    static float PF(FbxNode p, int i) => i < p.Props.Count ? Convert.ToSingle(p.Props[i].Value) : 0f;
    static int   PI(FbxNode p, int i) => i < p.Props.Count ? Convert.ToInt32(p.Props[i].Value)  : 0;

    static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "Node";
        int sep = s.IndexOf("\x00\x01", StringComparison.Ordinal); if (sep >= 0) s = s[(sep + 2)..];
        sep = s.IndexOf("::", StringComparison.Ordinal);           if (sep >= 0) s = s[(sep + 2)..];
        return s.Trim();
    }
}

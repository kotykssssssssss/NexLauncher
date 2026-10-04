using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using NexLauncher.Models;
using SkiaSharp;

namespace NexLauncher.Services.Skins;

/// <summary>Small orthographic cuboid renderer using the Skia already shipped with Avalonia.</summary>
public sealed class SkinPreviewRenderer
{
    private sealed record Face(Vector3 A, Vector3 B, Vector3 C, Vector3 D, Vector3 Normal, int U, int V, int Width, int Height);
    public byte[] Render(SkinImage? skin, SkinModel model, double yaw, double zoom, CancellationToken token)
    {
        var pixels = skin?.Pixels ?? Mannequin();
        var faces = new List<Face>(); var arm = model == SkinModel.Slim ? 3 : 4;
        BodyPart(-4, 0, -4, 8, 8, 8, 0, 0, 32, 0, .5f);
        BodyPart(-4, 8, -2, 8, 12, 4, 16, 16, 16, 32, .25f);
        BodyPart(-4 - arm, 8, -2, arm, 12, 4, 40, 16, 40, 32, .25f);
        BodyPart(4, 8, -2, arm, 12, 4, 32, 48, 48, 48, .25f);
        BodyPart(-4, 20, -2, 4, 12, 4, 0, 16, 0, 32, .25f);
        BodyPart(0, 20, -2, 4, 12, 4, 16, 48, 0, 48, .25f);
        var cos = (float)Math.Cos(yaw); var sin = (float)Math.Sin(yaw);
        const float pitch = .18f; var cp = MathF.Cos(pitch); var sp = MathF.Sin(pitch);
        Vector3 Rotate(Vector3 p) { var x = p.X * cos + p.Z * sin; var z = p.Z * cos - p.X * sin; return new(x, p.Y * cp - z * sp, z * cp + p.Y * sp); }
        Vector3 Position(Vector3 p) => Rotate(p - new Vector3(0, 16, 0));
        var scale = 11 * (float)Math.Clamp(zoom, .6, 1.6);
        SKPoint Project(Vector3 p) { var q = Position(p); return new(256 + q.X * scale, 240 + q.Y * scale); }
        using var surface = SKSurface.Create(new SKImageInfo(512, 512, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas; canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        paint.Color = new SKColor(0, 0, 0, 60); canvas.DrawOval(256, 432, 90, 12, paint);
        using var path = new SKPath();
        foreach (var face in faces.Where(f => Rotate(f.Normal).Z < -.001f).OrderByDescending(f => Position((f.A + f.B + f.C + f.D) / 4).Z))
        {
            token.ThrowIfCancellationRequested();
            var shade = face.Normal.Y < 0 ? 1f : face.Normal.X != 0 ? .8f : .94f;
            Vector3 Point(float x, float y) => Vector3.Lerp(Vector3.Lerp(face.A, face.B, x), Vector3.Lerp(face.D, face.C, x), y);
            for (var y = 0; y < face.Height; y++) for (var x = 0; x < face.Width; x++)
            {
                var offset = ((face.V + y) * 64 + face.U + x) * 4; var alpha = pixels[offset + 3];
                if (alpha == 0) continue;
                paint.Color = new SKColor((byte)(pixels[offset] * shade), (byte)(pixels[offset + 1] * shade), (byte)(pixels[offset + 2] * shade), alpha);
                var x0 = x / (float)face.Width; var x1 = (x + 1) / (float)face.Width;
                var y0 = y / (float)face.Height; var y1 = (y + 1) / (float)face.Height;
                path.Reset(); path.MoveTo(Project(Point(x0, y0))); path.LineTo(Project(Point(x1, y0)));
                path.LineTo(Project(Point(x1, y1))); path.LineTo(Project(Point(x0, y1))); path.Close(); canvas.DrawPath(path, paint);
            }
        }
        using var result = surface.Snapshot(); using var png = result.Encode(SKEncodedImageFormat.Png, 100); return png.ToArray();

        void BodyPart(float x, float y, float z, int w, int h, int d, int u, int v, int outerU, int outerV, float inflate)
        { Box(x, y, z, w, h, d, u, v, 0); Box(x, y, z, w, h, d, outerU, outerV, inflate); }
        void Box(float x, float y, float z, int w, int h, int d, int u, int v, float inflate)
        {
            var l = x - inflate; var r = x + w + inflate; var t = y - inflate; var b = y + h + inflate;
            var near = z - inflate; var far = z + d + inflate;
            faces.Add(new(new(l,t,near),new(r,t,near),new(r,b,near),new(l,b,near),new(0,0,-1),u+d,v+d,w,h));
            faces.Add(new(new(r,t,far),new(l,t,far),new(l,b,far),new(r,b,far),new(0,0,1),u+2*d+w,v+d,w,h));
            faces.Add(new(new(l,t,far),new(l,t,near),new(l,b,near),new(l,b,far),new(-1,0,0),u,v+d,d,h));
            faces.Add(new(new(r,t,near),new(r,t,far),new(r,b,far),new(r,b,near),new(1,0,0),u+d+w,v+d,d,h));
            faces.Add(new(new(l,t,far),new(r,t,far),new(r,t,near),new(l,t,near),new(0,-1,0),u+d,v,w,d));
            faces.Add(new(new(l,b,near),new(r,b,near),new(r,b,far),new(l,b,far),new(0,1,0),u+d+w,v,w,d));
        }
    }
    private static byte[] Mannequin()
    {
        var pixels = new byte[64 * 64 * 4];
        foreach (var area in new[] { (0,0,32,16), (0,16,64,32), (16,48,48,64) })
            for (var y = area.Item2; y < area.Item4; y++) for (var x = area.Item1; x < area.Item3; x++)
            { var i = (y * 64 + x) * 4; pixels[i] = 110; pixels[i+1] = 130; pixels[i+2] = 145; pixels[i+3] = 255; }
        return pixels;
    }
}

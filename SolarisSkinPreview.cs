using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Microsoft.Win32;

namespace SolarisLauncher;

public partial class MainWindow
{
    private BitmapSource? _skinTexture;
    private double _skinRotation = 0.35;
    private Point _skinDragPoint;
    private bool _skinDragging;

    private string CurrentSkinPath()
    {
        string username = WelcomeText.Text;
        string safe = new string(username.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (_cloudSession is { } cloud &&
            username.Equals(cloud.Nickname, StringComparison.OrdinalIgnoreCase))
            safe = "cloud-" + new string(cloud.Id.ToLowerInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return Path.Combine(_stateDir, "profiles", safe, "skin.png");
    }

    private async Task PrepareSkinPreviewAsync(string username)
    {
        // One texture is the source of truth for the full-body previews and
        // for the compact profile head. Never depend on a removed avatar picker.
        BitmapSource image = CreateDefaultSkin();
        bool hasPlayerSkin = false;
        string local = CurrentSkinPath();
        try
        {
            if (File.Exists(local))
            {
                image = DecodeSkin(await File.ReadAllBytesAsync(local));
                hasPlayerSkin = true;
            }
            else if (!string.IsNullOrWhiteSpace(username))
            {
                // Public skins are optional. Offline/local users keep the
                // normal fallback; network failure must never block login.
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                byte[] remote = await client.GetByteArrayAsync(
                    "https://mc-heads.net/skin/" + Uri.EscapeDataString(username));
                image = DecodeSkin(remote);
                hasPlayerSkin = true;
            }
        }
        catch { /* Invalid skin or network problem: retain default full-body preview. */ }
        if (!string.Equals(WelcomeText.Text, username, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(CurrentSkinPath(), local, StringComparison.OrdinalIgnoreCase) ||
            MainView.Visibility != Visibility.Visible)
            return;

        _skinTexture = image;
        SetProfileSkinHead(hasPlayerSkin ? image : null);
        Show2DSkin(image);
        Build3DSkin(image);
    }

    private void SetProfileSkinHead(BitmapSource? texture)
    {
        // A branded Solaris fallback is shown only until a usable skin exists.
        // Use the Minecraft front face UVs, including the transparent hat layer.
        if (texture is not null && texture.PixelWidth >= 48 && texture.PixelHeight >= 16)
        {
            try
            {
                var baseFace = new CroppedBitmap(texture, new Int32Rect(8, 8, 8, 8));
                var hatLayer = new CroppedBitmap(texture, new Int32Rect(40, 8, 8, 8));
                var drawing = new DrawingVisual();
                using (DrawingContext context = drawing.RenderOpen())
                {
                    RenderOptions.SetBitmapScalingMode(drawing, BitmapScalingMode.NearestNeighbor);
                    context.DrawImage(baseFace, new Rect(0, 0, 8, 8));
                    context.DrawImage(hatLayer, new Rect(0, 0, 8, 8));
                }
                var head = new RenderTargetBitmap(8, 8, 96, 96, PixelFormats.Pbgra32);
                head.Render(drawing);
                head.Freeze();
                ProfileAvatarImage.Source = head;
                ProfileAvatarImage.Visibility = Visibility.Visible;
                ProfileAvatarFallback.Visibility = Visibility.Collapsed;
                return;
            }
            catch { /* Unexpected texture format: show the Solaris fallback. */ }
        }
        ProfileAvatarImage.Source = null;
        ProfileAvatarImage.Visibility = Visibility.Collapsed;
        ProfileAvatarFallback.Visibility = Visibility.Visible;
    }

    private static BitmapSource DecodeSkin(byte[] content)
    {
        if (content.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Файл скина слишком большой.");
        using var stream = new MemoryStream(content);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.PixelWidth != 64 || frame.PixelHeight != 64)
            throw new InvalidDataException("Поддерживается PNG-скин размером 64×64.");
        frame.Freeze();
        return frame;
    }

    private static BitmapSource CreateDefaultSkin()
    {
        const int w = 64, h = 64;
        byte[] pixels = new byte[w * h * 4];
        void RectFill(int x, int y, int width, int height, byte r, byte g, byte b)
        {
            for (int row = y; row < y + height; row++)
            for (int col = x; col < x + width; col++)
            {
                int p = (row * w + col) * 4;
                pixels[p] = b; pixels[p + 1] = g;
                pixels[p + 2] = r; pixels[p + 3] = 255;
            }
        }
        // Built-in Steve-inspired fallback; generated locally with no network.
        RectFill(0, 8, 32, 8, 188, 137, 101);
        RectFill(8, 8, 8, 3, 67, 44, 33);
        RectFill(9, 12, 2, 1, 44, 72, 147);
        RectFill(13, 12, 2, 1, 44, 72, 147);
        RectFill(16, 0, 16, 8, 70, 44, 34);
        RectFill(16, 20, 24, 12, 49, 169, 178);
        RectFill(20, 20, 8, 12, 51, 162, 173);
        RectFill(40, 20, 16, 12, 55, 158, 164);
        RectFill(44, 26, 4, 6, 187, 133, 100);
        RectFill(4, 20, 16, 12, 53, 69, 158);
        RectFill(4, 28, 4, 4, 71, 62, 51);
        RectFill(20, 52, 4, 12, 55, 67, 145);
        RectFill(36, 52, 4, 12, 177, 132, 102);
        var bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private void Show2DSkin(BitmapSource texture)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            void Part(int sx, int sy, int sw, int sh, int dx, int dy)
            {
                var fragment = new CroppedBitmap(texture, new Int32Rect(sx, sy, sw, sh));
                dc.DrawImage(fragment, new Rect(dx * 8, dy * 8, sw * 8, sh * 8));
            }
            Part(8, 8, 8, 8, 4, 0); // head
            Part(40, 8, 8, 8, 4, 0); // head overlay (transparent on custom skins)
            Part(20, 20, 8, 12, 4, 8); // torso
            Part(44, 20, 4, 12, 0, 8); // right arm
            Part(36, 52, 4, 12, 12, 8); // left arm
            Part(4, 20, 4, 12, 4, 20); // right leg
            Part(20, 52, 4, 12, 8, 20); // left leg
        }
        var result = new RenderTargetBitmap(128, 256, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        ProfileFullSkin2D.Source = result;
        RenderOptions.SetBitmapScalingMode(ProfileFullSkin2D, BitmapScalingMode.NearestNeighbor);
    }

    private static Material SkinMaterial(BitmapSource source, Rect region)
    {
        var brush = new ImageBrush(source)
        {
            Viewbox = region,
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
            TileMode = TileMode.None
        };
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }

    private static void AddFace(Model3DGroup group, BitmapSource skin,
        Point3D a, Point3D b, Point3D c, Point3D d, Rect texture)
    {
        var mesh = new MeshGeometry3D();
        mesh.Positions.Add(a); mesh.Positions.Add(b);
        mesh.Positions.Add(c); mesh.Positions.Add(d);
        mesh.TextureCoordinates.Add(new Point(0, 1));
        mesh.TextureCoordinates.Add(new Point(1, 1));
        mesh.TextureCoordinates.Add(new Point(1, 0));
        mesh.TextureCoordinates.Add(new Point(0, 0));
        mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(1); mesh.TriangleIndices.Add(2);
        mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(2); mesh.TriangleIndices.Add(3);
        Material faceMaterial = SkinMaterial(skin, texture);
        group.Children.Add(new GeometryModel3D(mesh, faceMaterial) { BackMaterial = faceMaterial });
    }

    private static void AddLimb(Model3DGroup group, BitmapSource skin,
        double x, double y, double z, double width, double height, double depth,
        int textureX, int textureY)
    {
        double l = x - width / 2, r = x + width / 2;
        double b = y - height / 2, t = y + height / 2;
        double n = z + depth / 2, f = z - depth / 2;
        int iw = (int)width, ih = (int)height, id = (int)depth;
        // Conventional Minecraft skin UV mapping: left/top/right/front/back/bottom.
        AddFace(group, skin, new(l,b,n), new(r,b,n), new(r,t,n), new(l,t,n),
            new(textureX + id, textureY + id, iw, ih));
        AddFace(group, skin, new(r,b,f), new(l,b,f), new(l,t,f), new(r,t,f),
            new(textureX + id + iw + id, textureY + id, iw, ih));
        AddFace(group, skin, new(r,b,n), new(r,b,f), new(r,t,f), new(r,t,n),
            new(textureX + id + iw, textureY + id, id, ih));
        AddFace(group, skin, new(l,b,f), new(l,b,n), new(l,t,n), new(l,t,f),
            new(textureX, textureY + id, id, ih));
        AddFace(group, skin, new(l,t,n), new(r,t,n), new(r,t,f), new(l,t,f),
            new(textureX + id, textureY, iw, id));
        AddFace(group, skin, new(l,b,f), new(r,b,f), new(r,b,n), new(l,b,n),
            new(textureX + id + iw, textureY, iw, id));
    }

    private void Build3DSkin(BitmapSource image)
    {
        var models = new Model3DGroup();
        models.Children.Add(new AmbientLight(Color.FromRgb(194, 194, 204)));
        models.Children.Add(new DirectionalLight(Color.FromRgb(255, 238, 211),
            new Vector3D(-1, -1, -2)));
        AddLimb(models, image, 0, 28, 0, 8, 8, 8, 0, 0); // head
        AddLimb(models, image, 0, 18, 0, 8, 12, 4, 16, 16); // torso
        AddLimb(models, image, -6, 18, 0, 4, 12, 4, 40, 16); // right arm
        AddLimb(models, image, 6, 18, 0, 4, 12, 4, 32, 48); // left arm
        AddLimb(models, image, -2, 6, 0, 4, 12, 4, 0, 16); // right leg
        AddLimb(models, image, 2, 6, 0, 4, 12, 4, 16, 48); // left leg
        ProfileFullSkin3D.Children.Clear();
        ProfileFullSkin3D.Children.Add(new ModelVisual3D { Content = models });
        UpdateSkinCamera();
    }

    private void UpdateSkinCamera()
    {
        double x = Math.Sin(_skinRotation) * 54;
        double z = Math.Cos(_skinRotation) * 54;
        var position = new Point3D(x, 20, z);
        var target = new Point3D(0, 16, 0);
        ProfileFullSkin3D.Camera = new PerspectiveCamera(position,
            target - position, new Vector3D(0, 1, 0), 39);
    }

    private void ProfileSkin2D_Click(object sender, RoutedEventArgs e)
    {
        ProfileFullSkin2D.Visibility = Visibility.Visible;
        ProfileFullSkin3D.Visibility = Visibility.Collapsed;
    }

    private void ProfileSkin3D_Click(object sender, RoutedEventArgs e)
    {
        ProfileFullSkin2D.Visibility = Visibility.Collapsed;
        ProfileFullSkin3D.Visibility = Visibility.Visible;
    }

    private async void ProfileSkinImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите PNG-скин Minecraft 64×64",
            Filter = "Minecraft skin (*.png)|*.png",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            byte[] data = File.ReadAllBytes(dialog.FileName);
            BitmapSource image = DecodeSkin(data);
            string destination = CurrentSkinPath();
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, data);
            _skinTexture = image;
            SetProfileSkinHead(image);
            Show2DSkin(image);
            Build3DSkin(image);
            if (_cloudSession is not null)
            {
                string marker = Path.Combine(Path.GetDirectoryName(destination)!, "skin-pending.flag");
                await File.WriteAllTextAsync(marker, "pending");
                await UploadCloudSkinAsync(data);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Solaris — скин",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ProfileModel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _skinDragging = true;
        _skinDragPoint = e.GetPosition(ProfileFullSkin3D);
        ProfileFullSkin3D.CaptureMouse();
    }

    private void ProfileModel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_skinDragging) return;
        Point next = e.GetPosition(ProfileFullSkin3D);
        _skinRotation += (next.X - _skinDragPoint.X) / 90.0;
        _skinDragPoint = next;
        UpdateSkinCamera();
    }

    private void ProfileModel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _skinDragging = false;
        ProfileFullSkin3D.ReleaseMouseCapture();
    }
}

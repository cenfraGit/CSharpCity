using System.Numerics;
using CSharpCity.Layout;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace CSharpCity.Render;

/// <summary>Opens the window and walks you around a <see cref="SceneGraph"/>.</summary>
public sealed class CityWindow : IDisposable
{
    readonly SceneGraph _scene;
    readonly string _title;

    IWindow _window = null!;
    GL _gl = null!;
    IInputContext _input = null!;
    BoxRenderer _boxes = null!;
    RoadRenderer _roads = null!;
    TerrainRenderer? _terrain;
    SkyRenderer _sky = null!;
    PostProcess _post = null!;
    /// <summary>Separate buffer from the static city: cars move, so this is re-uploaded each frame.</summary>
    BoxRenderer _traffic = null!;
    BoxRenderer.Instance[] _carInstances = Array.Empty<BoxRenderer.Instance>();
    /// <summary>Metres beyond which a route's traffic is skipped entirely.</summary>
    const float TrafficViewDistance = 260f;
    /// <summary>Ceiling on animated travellers per frame; each one is CPU work every single frame.</summary>
    const int MaxVisibleTravellers = 1500;

    // --- live traffic ---
    /// <summary>The cars. A real simulation, stepped with a dt, not a function of the clock.</summary>
    TrafficSim _sim = null!;
    CarRenderer _cars = null!;
    CarRenderer.Instance[] _carParts = Array.Empty<CarRenderer.Instance>();
    int _visibleCars;
    /// <summary>The car the camera is riding in, or -1.</summary>
    int _povCar = -1;

    (Vector3 Centre, float Radius)[] _pathBounds = Array.Empty<(Vector3, float)>();
    readonly List<BoxRenderer.Chunk> _chunks = new();
    int _drawnInstances, _totalInstances, _visibleLabels, _visibleTravellers;
    int _framesThisSecond;
    double _secondAccumulator;

    // --- cinematic tour ---
    const double FlightSeconds = 3.2;
    /// <summary>Visit order, shuffled once, so the tour feels random but never repeats a stop early.</summary>
    int[] _tourOrder = Array.Empty<int>();
    int _tourIndex = -1;
    bool _inFlight;
    double _flightElapsed;
    Vector3 _flightFrom, _flightTo;
    float _flightFromYaw, _flightToYaw, _flightFromPitch, _flightToPitch;
    PointOfInterest? _arrivedAt;
    double _captionSeconds;

    // --- ride-along ---
    bool _riding;
    TextRenderer _text = null!;
    FontAtlas _atlas = null!;
    HudRenderer _hud = null!;
    double _elapsed;
    bool _trafficVisible = true;
    bool _minimapVisible = true;
    /// <summary>Off by default: the crosshair and its card are for inspecting, not for sightseeing.</summary>
    bool _inspectVisible;
    /// <summary>On by default, so the layer keys are discoverable without reading the console.</summary>
    bool _legendVisible = true;
    bool _worstVisible;
    /// <summary>
    /// Smog and the footpath surfaces start off — both blanket the view, and there is one path per
    /// dependency. The walkers stay on: they're the ones carrying the meaning, and a crowd streaming
    /// between two buildings reads fine without the worn ground under it.
    /// </summary>
    CityLayer _layers = CityLayer.Walkers | CityLayer.Roundabouts
                        | CityLayer.Highways | CityLayer.Air | CityLayer.Sidewalks;
    Camera _camera = null!;
    bool _labelsVisible = true;

    Vector2 _lastMouse;
    bool _firstMouse = true;
    bool _mouseCaptured = true;
    float _night;          // 0 = day (structural view), 1 = night (public-API view)
    bool _nightTarget;
    int _hoveredPickId = -1;

    public CityWindow(SceneGraph scene, string title)
    {
        _scene = scene;
        _title = title;
    }

    public void Run()
    {
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(1600, 900);
        options.Title = _title;
        options.VSync = true;
        options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default,
            new APIVersion(3, 3));

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.FramebufferResize += size => _gl.Viewport(size);
        _window.Closing += OnClosing;
        _window.Run();
    }

    void OnLoad()
    {
        _gl = GL.GetApi(_window);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _boxes = new BoxRenderer(_gl);
        var instances = BuildInstances();
        _totalInstances = instances.Length;
        _boxes.Upload(instances);

        _sky = new SkyRenderer(_gl);
        _post = new PostProcess(_gl, _window.FramebufferSize.X, _window.FramebufferSize.Y);

        if (_scene.Terrain is { Indices.Length: > 0 } mesh)
            _terrain = new TerrainRenderer(_gl, mesh);

        _roads = new RoadRenderer(_gl);
        _roads.Upload(_scene.Roads, _layers);

        _traffic = new BoxRenderer(_gl);
        // Each traveller is built from several boxes now, so the buffer scales with parts.
        _carInstances = new BoxRenderer.Instance[
            Math.Min(_scene.Travellers.Count, MaxVisibleTravellers) * TravellerShapes.MaxParts];

        _cars = new CarRenderer(_gl);
        _sim = new TrafficSim(_scene.RoadNetwork, _scene.CarSpawns);
        // Simulated cars first, then the highway's scripted traffic, then the signal lamps — all in
        // one rebuilt-every-frame buffer. Sized with room for all three so a busy junction full of
        // lamps can never crowd out the cars, which are emitted first and matter more.
        _carParts = new CarRenderer.Instance[_sim.TargetPopulation * 2 * CarShapes.MaxParts + 6144];

        // A bounding sphere per route, so a whole path's traffic can be rejected without sampling
        // every traveller on it.
        _pathBounds = new (Vector3 Centre, float Radius)[_scene.Paths.Count];
        for (int i = 0; i < _scene.Paths.Count; i++)
        {
            var points = _scene.Paths[i].Points;
            if (points.Length == 0) continue;

            var centre = Vector3.Zero;
            foreach (var point in points) centre += point;
            centre /= points.Length;

            float radius = 0f;
            foreach (var point in points) radius = MathF.Max(radius, Vector3.Distance(centre, point));
            _pathBounds[i] = (centre, radius);
        }

        _atlas = new FontAtlas(_gl);
        _text = new TextRenderer(_gl, _atlas);
        _text.Build(_scene.Labels);
        _hud = new HudRenderer(_gl, _atlas);

        _camera = new Camera { Position = _scene.SpawnPosition, Yaw = _scene.SpawnYaw };

        _input = _window.CreateInput();
        foreach (var keyboard in _input.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
            keyboard.KeyChar += OnKeyChar;
        }
        foreach (var mouse in _input.Mice)
        {
            mouse.Cursor.CursorMode = CursorMode.Raw;
            mouse.MouseMove += OnMouseMove;
            mouse.Scroll += OnScroll;
        }

        Console.WriteLine($"{_scene.Labels.Count} labels · {_scene.Roads.Count} roads · " +
                          $"{_scene.Paths.Count} routes · {_scene.Travellers.Count} travellers");
        Console.WriteLine("WASD move · mouse look · Shift sprint · F fly · Tab day/night · " +
                          "L labels · T traffic · M minimap · Esc release cursor");
        Console.WriteLine($"F1 shows the key legend. C flies you to the next of " +
                          $"{_scene.Interest.Count} incidents around the city.");
        Console.WriteLine($"R rides a car to a building of your choosing " +
                          $"({_scene.CarSpawns.Count:n0} to pick from) — or anywhere, endlessly. " +
                          $"= fast-forwards the traffic.");
    }

    /// <summary>
    /// Flattens the scene into GPU instances, grouped into spatial chunks so whole neighbourhoods
    /// can be frustum-rejected without re-uploading anything per frame.
    /// </summary>
    /// <remarks>
    /// Ordering matters twice over. Instances are sorted by translucency first so alpha blending
    /// stays roughly correct, then by chunk within each pass — which keeps every chunk a contiguous
    /// range of the instance buffer, the thing that makes ranged drawing possible at all.
    /// </remarks>
    BoxRenderer.Instance[] BuildInstances()
    {
        var instances = new List<BoxRenderer.Instance>(_scene.Boxes.Count + _scene.Ground.Count + 1);

        // Bedrock, well below everything. The terrain mesh is the visible ground now; this only
        // backstops the view past the mountains, so it sits far enough down to never compete for
        // depth with anything you can actually see.
        var b = _scene.CityBounds;
        instances.Add(new BoxRenderer.Instance
        {
            BasePosition = new Vector3(b.CenterX, -9f, b.CenterZ),
            Size = new Vector3(MathF.Max(b.Width, 200f) * 3f, 6f, MathF.Max(b.Depth, 200f) * 3f),
            Color = new Vector4(0.14f, 0.15f, 0.16f, 1f),
            Flags = 0,
        });

        foreach (var quad in _scene.Ground)
        {
            instances.Add(new BoxRenderer.Instance
            {
                // Thick plates: the district floor has to clear the terrain beneath it by more than
                // the depth buffer can resolve at altitude, not by a token few centimetres.
                BasePosition = quad.BasePosition with { Y = -0.5f },
                Size = new Vector3(quad.Size.X, 0.6f, quad.Size.Y),
                Color = quad.Color,
                Flags = 0,
                Detail = 1f,
            });
        }

        foreach (var box in _scene.Boxes)
        {
            // Layered scenery can be switched off. Everything untagged is Always, so the city
            // proper is never filtered.
            if (box.Layer != CityLayer.Always && (box.Layer & _layers) == 0) continue;

            instances.Add(new BoxRenderer.Instance
            {
                BasePosition = box.BasePosition,
                Size = box.Size,
                Color = box.Color,
                Flags = box.Flags,
                Detail = box.Detail,
                Damage = box.Damage,
            });
        }

        return Chunkify(instances);
    }

    /// <summary>
    /// Sorts instances into (opaque-first, then spatial chunk) order and records each chunk's range
    /// and bounding box into <see cref="_chunks"/>.
    /// </summary>
    BoxRenderer.Instance[] Chunkify(List<BoxRenderer.Instance> instances)
    {
        var bounds = _scene.CityBounds;
        // Around 24 chunks across the city: small enough that culling bites, large enough that the
        // per-chunk draw-call overhead stays negligible.
        float chunkSize = MathF.Max(60f, MathF.Max(bounds.Width, bounds.Depth) / 24f);
        // Scenery outside the city proper — the mountain ring — produces negative cell indices, so
        // both axes are shifted into positive space before being combined. Without the margin,
        // cell (-1, 5) and cell (columns-1, 4) collapse to the same key and two distant chunks merge
        // into one enormous box that always survives the frustum test.
        const int Margin = 12;
        int columns = Math.Max(1, (int)MathF.Ceiling(bounds.Width / chunkSize) + 1) + Margin * 2;

        int ChunkOf(BoxRenderer.Instance instance)
        {
            int x = (int)MathF.Floor((instance.BasePosition.X - bounds.X) / chunkSize) + Margin;
            int z = (int)MathF.Floor((instance.BasePosition.Z - bounds.Z) / chunkSize) + Margin;
            return Math.Max(0, z) * columns + Math.Max(0, x);
        }

        // The bedrock plane is enormous and centred on the city; leave it at index 0 in its own
        // chunk so its bounds never inflate a real neighbourhood's box into "always visible".
        var ordered = instances
            .Select((instance, index) => (instance, index))
            .OrderBy(e => e.index == 0 ? 0 : 1)
            .ThenBy(e => e.instance.Color.W < 0.999f ? 1 : 0)
            .ThenBy(e => e.index == 0 ? 0 : ChunkOf(e.instance))
            .Select(e => e.instance)
            .ToArray();

        _chunks.Clear();
        int start = 0;
        for (int i = 1; i <= ordered.Length; i++)
        {
            bool boundary = i == ordered.Length
                            || i == 1                                  // bedrock stands alone
                            || (ordered[i].Color.W < 0.999f) != (ordered[i - 1].Color.W < 0.999f)
                            || ChunkOf(ordered[i]) != ChunkOf(ordered[i - 1]);
            if (!boundary) continue;

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            for (int j = start; j < i; j++)
            {
                var b = ordered[j];
                // BasePosition is the centre of the base, so the box spans half its width each way.
                var lo = new Vector3(b.BasePosition.X - b.Size.X * 0.5f, b.BasePosition.Y,
                    b.BasePosition.Z - b.Size.Z * 0.5f);
                var hi = new Vector3(b.BasePosition.X + b.Size.X * 0.5f, b.BasePosition.Y + b.Size.Y,
                    b.BasePosition.Z + b.Size.Z * 0.5f);
                min = Vector3.Min(min, lo);
                max = Vector3.Max(max, hi);
            }

            _chunks.Add(new BoxRenderer.Chunk(start, i - start, min, max,
                ordered[start].Color.W < 0.999f));
            start = i;
        }

        return ordered;
    }

    /// <summary>
    /// Typing into the picker. Subscribed to the character event rather than the key event so that
    /// shift, symbols and the user's own keyboard layout all work without being reimplemented.
    /// </summary>
    void OnKeyChar(IKeyboard keyboard, char character)
    {
        if (!_pickerOpen) return;
        // The font atlas covers printable ASCII, and so do .NET type names.
        if (character < ' ' || character > '~') return;

        _query += character;
        _pickerSelection = 0;
        RefilterDestinations();
    }

    void OnKeyDown(IKeyboard keyboard, Key key, int _)
    {
        // While the picker is open it takes every key. Otherwise typing a building's name would
        // toggle labels, launch the tour and fly the camera off across the city.
        if (_pickerOpen)
        {
            switch (key)
            {
                case Key.Escape: ClosePicker(); break;
                case Key.Enter or Key.KeypadEnter: ConfirmPicker(); break;
                case Key.Backspace:
                    if (_query.Length > 0)
                    {
                        _query = _query[..^1];
                        RefilterDestinations();
                    }
                    break;
                case Key.Up:
                    _pickerSelection = Math.Max(0, _pickerSelection - 1);
                    ScrollToSelection();
                    break;
                case Key.Down:
                    _pickerSelection = Math.Min(_matches.Count, _pickerSelection + 1);
                    ScrollToSelection();
                    break;
                case Key.PageUp:
                    _pickerSelection = Math.Max(0, _pickerSelection - PickerRows);
                    ScrollToSelection();
                    break;
                case Key.PageDown:
                    _pickerSelection = Math.Min(_matches.Count, _pickerSelection + PickerRows);
                    ScrollToSelection();
                    break;
            }
            return;
        }

        switch (key)
        {
            case Key.Escape:
                _mouseCaptured = !_mouseCaptured;
                foreach (var mouse in _input.Mice)
                    mouse.Cursor.CursorMode = _mouseCaptured ? CursorMode.Raw : CursorMode.Normal;
                _firstMouse = true;
                break;
            case Key.F:
                _camera.ToggleFly();
                break;
            case Key.Tab:
                _nightTarget = !_nightTarget;
                break;
            case Key.L:
                _labelsVisible = !_labelsVisible;
                break;
            case Key.T:
                _trafficVisible = !_trafficVisible;
                break;
            case Key.M:
                _minimapVisible = !_minimapVisible;
                break;
            case Key.C:
                FlyToNextInterest();
                break;
            case Key.B:
                _worstVisible = !_worstVisible;
                break;
            case Key.R:
                ToggleRide();
                break;

            // The worst list is a shortlist you can act on: press its number to go there.
            case >= Key.Number1 and <= Key.Number9:
                FlyToWorst(key - Key.Number1);
                break;
            case Key.Number0:
                FlyToWorst(9);
                break;

            // Layer isolation: switch one channel off to see what the others are doing.
            case Key.F1:
                _legendVisible = !_legendVisible;
                break;
            case Key.F8:
                _inspectVisible = !_inspectVisible;
                break;
            case Key.F2: ToggleLayer(CityLayer.Smog); break;
            case Key.F3: ToggleLayer(CityLayer.Rail); break;
            case Key.F4: ToggleLayer(CityLayer.Roundabouts); break;
            case Key.F5: ToggleLayer(CityLayer.Footpaths); break;
            case Key.F6: ToggleLayer(CityLayer.Highways); break;
            case Key.F7: ToggleLayer(CityLayer.Air); break;
            case Key.F9: ToggleLayer(CityLayer.Walkers); break;
            case Key.F10: ToggleLayer(CityLayer.Sidewalks); break;
            case Key.Equal or Key.KeypadAdd:
                // Sub-stepped inside the simulation, so speeding up is more ticks and never a
                // bigger one — the car-following stays stable at any scale.
                _sim.TimeScale = _sim.TimeScale switch { < 1.5f => 2f, < 3f => 4f, < 6f => 8f, _ => 1f };
                ShowCaption($"Time x{_sim.TimeScale:0}", "");
                break;
            case Key.F11:
                // The post-process chain resizes itself every frame and the HUD reads the
                // framebuffer size fresh, so nothing else has to know this happened.
                _window.WindowState = _window.WindowState == WindowState.Fullscreen
                    ? WindowState.Normal
                    : WindowState.Fullscreen;
                break;
            case Key.Q:
                if (keyboard.IsKeyPressed(Key.ControlLeft)) _window.Close();
                break;
        }
    }

    /// <summary>
    /// Flies the camera to the next point of interest — a crime scene, a fire, a crane, a cycle.
    /// </summary>
    /// <remarks>
    /// The city is 1.4 km across and most of its incidents are a handful of buildings. Without this
    /// you can walk for minutes and find none of them. The order is shuffled once at startup rather
    /// than re-randomised per press, so repeated presses tour the whole city instead of landing on
    /// the same two scenes.
    /// </remarks>
    void FlyToNextInterest()
    {
        if (_scene.Interest.Count == 0) return;

        if (_tourOrder.Length != _scene.Interest.Count)
        {
            // Deterministic shuffle: same city, same tour, every run.
            _tourOrder = Enumerable.Range(0, _scene.Interest.Count).ToArray();
            for (int i = _tourOrder.Length - 1; i > 0; i--)
            {
                int j = (int)(Hash(i) * (i + 1)) % (i + 1);
                (_tourOrder[i], _tourOrder[j]) = (_tourOrder[j], _tourOrder[i]);
            }
            _tourIndex = -1;
        }

        _tourIndex = (_tourIndex + 1) % _tourOrder.Length;
        FlyTo(_scene.Interest[_tourOrder[_tourIndex]]);
    }

    // --- destination picker ---
    /// <summary>Every building a car can be sent to: name, project, and where it is.</summary>
    (string Display, string Project, Vector3 Centre, int Spawn)[] _destinations =
        Array.Empty<(string, string, Vector3, int)>();
    readonly List<int> _matches = new();
    bool _pickerOpen;
    string _query = "";
    int _pickerSelection;
    int _pickerScroll;
    /// <summary>Rows drawn at once. There is no scissor, so the list is sliced, not clipped.</summary>
    const int PickerRows = 16;

    void BuildDestinations()
    {
        var byId = _scene.Sites;
        var list = new List<(string, string, Vector3, int)>(_scene.CarSpawns.Count);

        for (int i = 0; i < _scene.CarSpawns.Count; i++)
        {
            var spawn = _scene.CarSpawns[i];
            if (spawn.PickId < 0 || spawn.PickId >= _scene.PickInfos.Count) continue;
            if (!byId.TryGetValue(spawn.TypeId, out var site)) continue;
            list.Add((_scene.PickInfos[spawn.PickId].DisplayName, site.ProjectName, site.Center, i));
        }

        _destinations = list.OrderBy(d => d.Item1, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    void OpenPicker()
    {
        if (_destinations.Length == 0) BuildDestinations();
        if (_destinations.Length == 0)
        {
            Console.WriteLine("No building in this city can be reached by road.");
            return;
        }

        _pickerOpen = true;
        _query = "";
        _pickerSelection = 0;
        _pickerScroll = 0;
        RefilterDestinations();

        // Release the mouse, or looking around while typing spins the camera.
        _mouseCaptured = false;
        foreach (var mouse in _input.Mice) mouse.Cursor.CursorMode = CursorMode.Normal;
        _firstMouse = true;
    }

    void ClosePicker()
    {
        _pickerOpen = false;
        _mouseCaptured = true;
        foreach (var mouse in _input.Mice) mouse.Cursor.CursorMode = CursorMode.Raw;
        _firstMouse = true;
    }

    /// <summary>
    /// Recomputes the visible rows. Called when the query changes and at no other time — a
    /// thousand-odd substring tests is nothing once, and pointless sixty times a second.
    /// </summary>
    void RefilterDestinations()
    {
        _matches.Clear();
        for (int i = 0; i < _destinations.Length; i++)
            if (_query.Length == 0 ||
                _destinations[i].Display.Contains(_query, StringComparison.OrdinalIgnoreCase))
                _matches.Add(i);

        _pickerSelection = Math.Clamp(_pickerSelection, 0, Math.Max(0, _matches.Count));
        ScrollToSelection();
    }

    void ScrollToSelection()
    {
        // The first row is always the cruise option, so the list index is one behind the row.
        int row = _pickerSelection;
        if (row < _pickerScroll) _pickerScroll = row;
        if (row >= _pickerScroll + PickerRows) _pickerScroll = row - PickerRows + 1;
        _pickerScroll = Math.Max(0, _pickerScroll);
    }

    void ConfirmPicker()
    {
        // Row zero is "anywhere": a journey that picks a new destination every time it arrives.
        if (_pickerSelection == 0)
        {
            ClosePicker();
            GoTo(-1, cruise: true, null);
            return;
        }

        int index = _pickerSelection - 1;
        if (index < 0 || index >= _matches.Count) return;

        var destination = _destinations[_matches[index]];
        ClosePicker();
        GoTo(destination.Spawn, cruise: false, destination);
    }

    /// <summary>
    /// Goes to a chosen building — by road on foot, or straight there if already in the air.
    /// </summary>
    void GoTo(int spawn, bool cruise, (string Display, string Project, Vector3 Centre, int Spawn)? at)
    {
        if (_camera.Flying && at is { } target)
        {
            // Flying skips the traffic entirely, exactly as the incident tour does: someone already
            // above the rooftops asked to see a place, not to sit through the drive.
            FlyTo(new PointOfInterest
            {
                Focus = target.Centre with { Y = 12f },
                Distance = 46f,
                Headline = target.Display,
                Detail = target.Project,
            });
            return;
        }

        BeginRide(spawn, cruise);
    }

    void ShowCaption(string headline, string detail)
    {
        _arrivedAt = new PointOfInterest { Headline = headline, Detail = detail };
        _captionSeconds = 0;
    }

    /// <summary>
    /// Ends the current ride, or opens the picker to start one.
    /// </summary>
    /// <remarks>
    /// The ride used to be a camera on a rail: a precomputed wander through the streets, played
    /// back at a fixed speed, ping-ponging when it reached the end. It went nowhere by design.
    ///
    /// Now the camera sits in an ordinary simulated car — same routing, same queueing, same red
    /// lights as the two hundred others — and that car is going somewhere you chose.
    /// </remarks>
    void ToggleRide()
    {
        if (_riding || _povCar >= 0)
        {
            StopRiding();
            return;
        }

        if (!_sim.CanDrive)
        {
            Console.WriteLine("No drivable road network in this city.");
            return;
        }

        OpenPicker();
    }

    void StopRiding()
    {
        if (_povCar >= 0) _sim.EndRide(_povCar);
        _povCar = -1;
        _riding = false;
    }

    /// <summary>Starts a journey to a chosen building, or an endless one if cruising.</summary>
    void BeginRide(int destinationSpawn, bool cruise)
    {
        _inFlight = false;
        StopRiding();

        int id = _sim.RequestRide(_camera.Position, destinationSpawn, cruise);
        if (id < 0)
        {
            Console.WriteLine("No route from here to there.");
            return;
        }

        _povCar = id;
        _riding = true;
        if (_camera.Flying) _camera.ToggleFly();
    }

    void AdvanceRide(double deltaTime)
    {
        if (!_riding || _povCar < 0) return;

        if (!_sim.TryGetCar(_povCar, out var car))
        {
            // Arrived, and the car stopped existing along with everyone else's.
            ShowCaption("You have arrived.", "");
            StopRiding();
            return;
        }

        // Eye height of someone sitting in a car, a little forward of centre, plus engine idle.
        var forward = new Vector3(MathF.Cos(car.Yaw), 0f, MathF.Sin(car.Yaw));
        _camera.Position = car.Position + forward * 0.4f + new Vector3(0f,
            1.35f + MathF.Sin((float)_elapsed * 9f) * 0.015f, 0f);

        float targetYaw = car.Yaw * 180f / MathF.PI;
        // Ease into the new heading so junctions are a turn rather than a snap.
        float delta = targetYaw - _camera.Yaw;
        while (delta > 180f) delta -= 360f;
        while (delta < -180f) delta += 360f;

        _camera.Yaw += delta * MathF.Min(1f, (float)deltaTime * 5.5f);
        // Follow the road's own slope, which the old rail camera could not do: it had no idea a
        // highway ramp was a slope at all.
        float targetPitch = car.Pitch * 180f / MathF.PI;
        _camera.Pitch += (targetPitch - _camera.Pitch) * MathF.Min(1f, (float)deltaTime * 3f);
    }

    void FlyToWorst(int rank)
    {
        if (!_worstVisible || rank < 0 || rank >= _scene.Worst.Count) return;

        var entry = _scene.Worst[rank];
        FlyTo(new PointOfInterest
        {
            Focus = entry.Position with { Y = 10f },
            Distance = 46f,
            Headline = $"#{rank + 1}  {entry.Name}",
            Detail = $"{entry.Project} · {entry.Reason}",
        });
    }

    void FlyTo(PointOfInterest stop)
    {
        // Approach from wherever we already are, so consecutive stops don't all look identical.
        var away = new Vector3(_camera.Position.X - stop.Focus.X, 0f, _camera.Position.Z - stop.Focus.Z);
        if (away.LengthSquared() < 1f) away = Vector3.UnitZ;
        away = Vector3.Normalize(away);

        _flightFrom = _camera.Position;
        _flightTo = stop.Focus + away * stop.Distance + new Vector3(0f, stop.Distance * 0.34f, 0f);

        var look = Vector3.Normalize(stop.Focus - _flightTo);
        _flightFromYaw = _camera.Yaw;
        _flightFromPitch = _camera.Pitch;
        _flightToYaw = MathF.Atan2(look.Z, look.X) * 180f / MathF.PI;
        _flightToPitch = MathF.Asin(Math.Clamp(look.Y, -1f, 1f)) * 180f / MathF.PI;

        // Turn the short way round, or the camera spins 350 degrees to travel 10.
        while (_flightToYaw - _flightFromYaw > 180f) _flightToYaw -= 360f;
        while (_flightToYaw - _flightFromYaw < -180f) _flightToYaw += 360f;

        _inFlight = true;
        _flightElapsed = 0;
        _arrivedAt = stop;
        _captionSeconds = 0;
        if (!_camera.Flying) _camera.ToggleFly();   // the route is rarely walkable
    }

    void AdvanceFlight(double deltaTime)
    {
        if (!_inFlight) return;

        _flightElapsed += deltaTime;
        float t = (float)Math.Clamp(_flightElapsed / FlightSeconds, 0, 1);
        // Ease in and out: a linear camera move reads as a machine, not a shot.
        float eased = t * t * (3f - 2f * t);

        // Arc upward through the middle of the flight so the camera clears the skyline en route.
        float lift = MathF.Sin(t * MathF.PI) * Vector3.Distance(_flightFrom, _flightTo) * 0.18f;

        _camera.Position = Vector3.Lerp(_flightFrom, _flightTo, eased) + new Vector3(0f, lift, 0f);
        _camera.Yaw = _flightFromYaw + (_flightToYaw - _flightFromYaw) * eased;
        _camera.Pitch = _flightFromPitch + (_flightToPitch - _flightFromPitch) * eased;

        if (t >= 1f) _inFlight = false;
    }

    static float Hash(int value)
    {
        unchecked
        {
            uint x = (uint)value * 2654435761u;
            x ^= x >> 15;
            x *= 2246822519u;
            return ((x ^ (x >> 13)) & 0xFFFFFF) / (float)0x1000000;
        }
    }

    /// <summary>
    /// Flips a layer and re-uploads the road buffer. Travellers filter per frame in
    /// <see cref="AdvanceTraffic"/>, so trains and walkers disappear with their track and paths.
    /// </summary>
    void ToggleLayer(CityLayer layer)
    {
        _layers ^= layer;
        _roads.Upload(_scene.Roads, _layers);

        // Boxes are chunked and uploaded once at startup, so a layer that has any boxes in it
        // means rebuilding that buffer. Only the sidewalks do, and only on a keypress.
        if (layer == CityLayer.Sidewalks)
        {
            var instances = BuildInstances();
            _totalInstances = instances.Length;
            _boxes.Upload(instances);
        }

        Console.WriteLine($"{layer}: {((_layers & layer) != 0 ? "on" : "off")}");
    }

    /// <summary>
    /// The wheel widens and narrows the view.
    /// </summary>
    /// <remarks>
    /// Scrolling the picker's list would be the other obvious use, but the list is driven from the
    /// keyboard and the wheel is worth more here: a wide angle makes a street feel like somewhere
    /// you are standing, a narrow one lets you read a nameplate across the city.
    /// </remarks>
    void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        if (_pickerOpen) return;
        _camera.Fov = Math.Clamp(_camera.Fov - wheel.Y * 2.5f, Camera.MinFov, Camera.MaxFov);
    }

    void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (!_mouseCaptured) return;
        if (_firstMouse) { _lastMouse = position; _firstMouse = false; return; }
        var delta = position - _lastMouse;
        _lastMouse = position;
        _camera.Look(delta.X, delta.Y);
    }

    void OnUpdate(double deltaTime)
    {
        var dt = (float)deltaTime;
        _elapsed += deltaTime;
        var keyboard = _input.Keyboards[0];

        var move = Vector3.Zero;
        // Nothing the keyboard does moves the camera while a name is being typed into it.
        if (_pickerOpen)
        {
            _sim.Step(dt);
            _night += Math.Clamp((_nightTarget ? 1f : 0f) - _night, -dt * 2f, dt * 2f);
            _captionSeconds += deltaTime;
            ReportPerformance(deltaTime);
            return;
        }

        if (keyboard.IsKeyPressed(Key.W)) move.Z += 1;
        if (keyboard.IsKeyPressed(Key.S)) move.Z -= 1;
        if (keyboard.IsKeyPressed(Key.D)) move.X += 1;
        if (keyboard.IsKeyPressed(Key.A)) move.X -= 1;
        if (keyboard.IsKeyPressed(Key.Space)) move.Y += 1;
        if (keyboard.IsKeyPressed(Key.ControlLeft)) move.Y -= 1;

        // Any movement input cancels the tour or the ride: you're never trapped in a cutscene.
        if (move != Vector3.Zero)
        {
            _inFlight = false;
            if (_riding) StopRiding();
        }

        // Traffic advances whether or not it is being drawn, so the city is not visibly frozen in
        // place the moment it comes back into view.
        _sim.Step(dt);

        AdvanceFlight(deltaTime);
        AdvanceRide(deltaTime);
        if (!_inFlight && !_riding) _camera.Move(move, dt, keyboard.IsKeyPressed(Key.ShiftLeft));
        _captionSeconds += deltaTime;

        // Ease between day and night rather than snapping.
        _night += Math.Clamp(((_nightTarget ? 1f : 0f) - _night), -dt * 2f, dt * 2f);

        UpdateHover();
        ReportPerformance(deltaTime);
    }

    /// <summary>
    /// Prints frame rate and how much of the city survived culling, once a second. This is the only
    /// way to tell whether the chunked draw is actually earning its complexity at scale.
    /// </summary>
    void ReportPerformance(double deltaTime)
    {
        _framesThisSecond++;
        _secondAccumulator += deltaTime;
        if (_secondAccumulator < 1.0) return;

        int total = _totalInstances;
        float percent = total == 0 ? 0f : _drawnInstances * 100f / total;
        _window.Title = $"{_title} — {_framesThisSecond} fps · " +
                        $"{_drawnInstances}/{total} boxes ({percent:F0}%) · " +
                        $"{_visibleLabels} labels · {_visibleTravellers} traffic";

        _framesThisSecond = 0;
        _secondAccumulator = 0;
    }

    /// <summary>CPU raycast from the crosshair against building AABBs — drives the inspection readout.</summary>
    void UpdateHover()
    {
        var origin = _camera.Position;
        var dir = _camera.Front;
        float best = 300f;
        int hit = -1;

        foreach (var box in _scene.Boxes)
        {
            if (box.PickId < 0) continue;
            var min = box.BasePosition - new Vector3(box.Size.X * 0.5f, 0, box.Size.Z * 0.5f);
            var max = min + box.Size;
            if (RayAabb(origin, dir, min, max, out float t) && t < best)
            {
                best = t;
                hit = box.PickId;
            }
        }

        if (hit == _hoveredPickId) return;
        _hoveredPickId = hit;

        // Until the bitmap-font HUD lands in Phase 5, the inspection card goes to the title bar.
        if (hit >= 0)
        {
            var info = _scene.PickInfos[hit];
            var smells = info.SmellLabels.Count > 0 ? "  ⚠ " + string.Join(", ", info.SmellLabels) : "";
            _window.Title = $"{info.DisplayName}  [{info.Kind}]  {info.Loc} LOC  cx {info.AvgComplexity:0.0}{smells}";
        }
        else
        {
            _window.Title = _title;
        }
    }

    static bool RayAabb(Vector3 origin, Vector3 dir, Vector3 min, Vector3 max, out float t)
    {
        // Slab method. A zero component in dir yields ±infinity, which the min/max below handle.
        var inv = new Vector3(1f / dir.X, 1f / dir.Y, 1f / dir.Z);
        var t1 = (min - origin) * inv;
        var t2 = (max - origin) * inv;
        var tNear = Vector3.Min(t1, t2);
        var tFar = Vector3.Max(t1, t2);
        float near = MathF.Max(MathF.Max(tNear.X, tNear.Y), tNear.Z);
        float far = MathF.Min(MathF.Min(tFar.X, tFar.Y), tFar.Z);
        t = near;
        return far >= MathF.Max(near, 0f);
    }

    void OnRender(double deltaTime)
    {
        var size = _window.FramebufferSize;
        float aspect = size.Y == 0 ? 1f : size.X / (float)size.Y;
        var viewProjection = _camera.View * _camera.Projection(aspect);

        // Everything from here to Resolve() lands in an HDR buffer, so highlights can exceed 1.0
        // and still be there for the bloom pass.
        _post.Resize(size.X, size.Y);
        _post.Time = (float)_elapsed;
        _post.Night = _night;
        _post.BeginScene();

        // The sky paints the whole frame before anything else, so it replaces the clear colour.
        _sky.Time = (float)_elapsed;
        _sky.Draw(viewProjection, _camera.Position, _night);

        var sun = new Vector3(0.4f, 0.85f, 0.3f);
        _boxes.Time = (float)_elapsed;
        _roads.Time = (float)_elapsed;
        var frustum = Frustum.FromViewProjection(viewProjection);
        // Terrain first: it's the backdrop everything else stands in front of.
        _terrain?.Draw(viewProjection, _camera.Position, sun, _night);
        _drawnInstances = _boxes.DrawChunks(viewProjection, _camera.Position, sun, _night, _chunks, frustum);
        _roads.Draw(viewProjection, _camera.Position, sun, _night);

        if (_trafficVisible && _carInstances.Length > 0)
        {
            _visibleTravellers = AdvanceTraffic();
            _traffic.Upload(_carInstances.AsSpan(0, _visibleTravellers));
            _traffic.Draw(viewProjection, _camera.Position, sun, _night);
        }

        if (_trafficVisible && _carParts.Length > 0)
        {
            _visibleCars = BuildCarInstances();
            _cars.Draw(_carParts.AsSpan(0, _visibleCars), viewProjection, _camera.Position, sun,
                _night);
        }

        if (_labelsVisible)
        {
            var right = _camera.Right;
            // True up for the billboard basis, not world up: labels must stay upright when you look down.
            var up = Vector3.Normalize(Vector3.Cross(right, _camera.Front));
            _text.Draw(viewProjection, _camera.Position, right, up);
            _visibleLabels = _text.VisibleCount;
        }

        // Bloom and tone map the world, then draw the HUD straight to the back buffer so text stays
        // crisp and doesn't glow.
        _post.Resolve();
        DrawHud();
    }

    /// <summary>
    /// Turns the simulation's cars into boxes, near ones first.
    /// </summary>
    /// <returns>How many instances were written into <see cref="_carParts"/>.</returns>
    int BuildCarInstances()
    {
        var eye = _camera.Position;
        int count = 0;

        foreach (var car in _sim.Cars())
        {
            // The car the camera is inside would fill the screen with its own roof.
            if (car.IsPov && _riding) continue;
            if (Vector3.Distance(eye, car.Position) > TrafficViewDistance) continue;
            if (count + CarShapes.MaxParts > _carParts.Length) break;

            count += CarShapes.Emit(_carParts.AsSpan(count), car);
        }

        count = AppendScriptedCars(count, eye);
        count = AppendAircraft(count, eye);
        return AppendSignalLamps(count, eye);
    }

    /// <summary>
    /// The three lamps in each signal head, lit according to the phase the junction is in.
    /// </summary>
    /// <remarks>
    /// Emitted per frame rather than baked with the housing, because the whole point of a signal
    /// is that it changes. They come through the car renderer for the same reason its vehicles do:
    /// it is the one buffer that is rebuilt every frame and the one shader that can rotate an
    /// instance, so a head can face back down the road it governs.
    ///
    /// The dark lamps are drawn too, not skipped. A head showing one lit lamp against two dead ones
    /// is legible from much further away than a single dot floating in the air.
    /// </remarks>
    /// <summary>
    /// Aircraft: aeroplanes on their circuits and the helicopter over the worst building.
    /// </summary>
    int AppendAircraft(int count, Vector3 eye)
    {
        float time = (float)_elapsed;

        for (int i = 0; i < _scene.Travellers.Count; i++)
        {
            var traveller = _scene.Travellers[i];
            if (traveller.Kind is not (TravellerKind.Plane or TravellerKind.Helicopter)) continue;
            if (traveller.Layer != CityLayer.Always && (traveller.Layer & _layers) == 0) continue;
            if (count + AircraftShapes.MaxParts > _carParts.Length) break;

            var path = _scene.Paths[traveller.PathIndex];
            if (path.Length <= 0.001f) continue;

            float distance = (traveller.Phase * path.Length + time * traveller.Speed) % path.Length;
            var (position, direction) = Sample(path, distance);
            // Aircraft are big and high, so they stay legible from much further off than a car.
            if (Vector3.Distance(eye, position) > TrafficViewDistance * 3f) continue;
            if (direction.LengthSquared() < 1e-6f) continue;

            count += AircraftShapes.Emit(_carParts.AsSpan(count), traveller, position,
                Vector3.Normalize(direction), time);
        }

        return count;
    }

    int AppendSignalLamps(int count, Vector3 eye)
    {
        var signals = _scene.RoadNetwork.Signals;
        if (signals.Length == 0) return count;

        float time = _sim.Now;
        var dark = new Vector4(0.09f, 0.09f, 0.10f, 1f);

        foreach (var head in _scene.SignalHeads)
        {
            if (Vector3.Distance(eye, head.Lamps) > TrafficViewDistance) continue;
            if (count + 3 > _carParts.Length) break;

            var signal = signals[head.SignalIndex];
            bool green = signal.IsGreen(time, head.ApproachRunsAlongX);
            bool amber = signal.IsAmber(time, head.ApproachRunsAlongX);

            // Top to bottom, as they are on a real head.
            AddLamp(ref count, head, 0.42f, !green && !amber
                ? new Vector4(0.95f, 0.16f, 0.12f, 1f) : dark);
            AddLamp(ref count, head, 0.0f, amber
                ? new Vector4(0.98f, 0.66f, 0.10f, 1f) : dark);
            AddLamp(ref count, head, -0.42f, green
                ? new Vector4(0.22f, 0.92f, 0.32f, 1f) : dark);
        }

        return count;
    }

    void AddLamp(ref int count, in SignalHead head, float height, Vector4 colour)
    {
        // Set into the front face of the housing, which the shader rotates with the head.
        var facing = new Vector3(MathF.Cos(head.Yaw), 0f, MathF.Sin(head.Yaw));
        _carParts[count++] = new CarRenderer.Instance
        {
            Center = head.Lamps + new Vector3(0f, height, 0f) + facing * 0.24f,
            Size = new Vector3(0.06f, 0.30f, 0.30f),
            Yaw = head.Yaw,
            Pitch = 0f,
            Color = colour,
            Flags = (uint)BoxFlags.Emissive,
        };
    }

    /// <summary>
    /// The highway deck's own traffic, drawn as proper vehicles rather than axis-snapped boxes.
    /// </summary>
    /// <remarks>
    /// These are travellers on a baked loop, not simulated agents, but they climb ramps — and a
    /// vehicle that can be on a slope needs a pitch, which the traveller shapes have no notion of.
    /// Routing them through the car renderer costs nothing and stops the ones on a ramp from
    /// lying flat while the road under them tilts.
    /// </remarks>
    int AppendScriptedCars(int count, Vector3 eye)
    {
        if ((_layers & CityLayer.Highways) == 0) return count;

        float time = (float)_elapsed;
        for (int i = 0; i < _scene.Travellers.Count; i++)
        {
            var traveller = _scene.Travellers[i];
            if (traveller.Layer != CityLayer.Highways) continue;
            if (traveller.Kind is not (TravellerKind.Car or TravellerKind.Truck)) continue;
            if (count + CarShapes.MaxParts > _carParts.Length) break;

            var path = _scene.Paths[traveller.PathIndex];
            if (path.Length <= 0.001f) continue;

            float distance = (traveller.Phase * path.Length + time * traveller.Speed) % path.Length;
            var (position, direction) = Sample(path, distance);
            if (Vector3.Distance(eye, position) > TrafficViewDistance) continue;
            if (direction.LengthSquared() < 1e-6f) continue;

            var heading = Vector3.Normalize(direction);
            count += CarShapes.Emit(_carParts.AsSpan(count), new CarAgent
            {
                Id = i,
                Position = position,
                Yaw = MathF.Atan2(heading.Z, heading.X),
                Pitch = MathF.Asin(Math.Clamp(heading.Y, -1f, 1f)),
                Speed = traveller.Speed,
                IsTruck = traveller.Kind == TravellerKind.Truck,
                Color = traveller.Color,
            });
        }

        return count;
    }

    /// <summary>
    /// Walks every traveller along its routed path for the current timestamp. Ordinary routes
    /// ping-pong end to end; roundabout paths loop, which is the whole point — a circular dependency
    /// is traffic that can never leave.
    /// </summary>
    /// <returns>How many travellers were written into <see cref="_carInstances"/>.</returns>
    int AdvanceTraffic()
    {
        float time = (float)_elapsed;
        var eye = _camera.Position;
        int count = 0;

        for (int i = 0; i < _scene.Travellers.Count; i++)
        {
            if (count == _carInstances.Length) break;

            var traveller = _scene.Travellers[i];
            if (traveller.Layer != CityLayer.Always && (traveller.Layer & _layers) == 0) continue;
            // Highway vehicles and aircraft are drawn by the car renderer instead, which can turn
            // and tilt them; drawing them here as well would double every one of them.
            if (traveller.Layer == CityLayer.Highways
                && traveller.Kind is TravellerKind.Car or TravellerKind.Truck) continue;
            if (traveller.Kind is TravellerKind.Plane or TravellerKind.Helicopter) continue;

            var path = _scene.Paths[traveller.PathIndex];
            if (path.Length <= 0.001f) continue;

            // Cull by route, not by traveller: people and cars are small enough to be sub-pixel well
            // before this, and skipping a whole path costs one distance test instead of dozens.
            var (centre, radius) = _pathBounds[traveller.PathIndex];
            if (Vector3.Distance(eye, centre) - radius > TrafficViewDistance) continue;

            float distance;
            if (path.Loop)
            {
                distance = (traveller.Phase * path.Length + time * traveller.Speed) % path.Length;
            }
            else
            {
                // Triangle wave over twice the length, so traffic runs both ways on one route.
                float cycle = (traveller.Phase * 2f * path.Length + time * traveller.Speed)
                              % (2f * path.Length);
                distance = cycle <= path.Length ? cycle : 2f * path.Length - cycle;
            }

            var (position, direction) = Sample(path, distance);

            // Stop before a partly-written figure: half a pedestrian is worse than none.
            if (count + TravellerShapes.MaxParts > _carInstances.Length) break;

            // The list index is the traveller's stable identity, so clothing and hats stay put.
            count += TravellerShapes.Emit(_carInstances.AsSpan(count), traveller, position,
                direction, distance, i);
        }

        return count;
    }

    /// <summary>Position and heading at a given arc length along a routed path.</summary>
    static (Vector3 Position, Vector3 Direction) Sample(TrafficPath path, float distance)
    {
        var points = path.Points;
        var cumulative = path.Cumulative;

        int segment = 1;
        while (segment < points.Length - 1 && cumulative[segment] < distance) segment++;

        float start = cumulative[segment - 1];
        float span = cumulative[segment] - start;
        float t = span > 0.001f ? (distance - start) / span : 0f;

        var a = points[segment - 1];
        var b = points[segment];
        return (Vector3.Lerp(a, b, t), b - a);
    }

    /// <summary>
    /// Crosshair, the inspection card for whatever it's on, and the minimap.
    /// </summary>
    void DrawHud()
    {
        var size = _window.FramebufferSize;
        var viewport = new Vector2(size.X, size.Y);
        _hud.Begin(viewport);

        // Crosshair: two thin bars, so it reads against both bright pavement and dark facades.
        if (_inspectVisible)
        {
            var centre = viewport * 0.5f;
            var ink = new Vector4(1f, 1f, 1f, 0.75f);
            _hud.Rect(centre.X - 7f, centre.Y - 1f, 14f, 2f, ink);
            _hud.Rect(centre.X - 1f, centre.Y - 7f, 2f, 14f, ink);

            if (_hoveredPickId >= 0 && _hoveredPickId < _scene.PickInfos.Count)
                DrawInspectionCard(_scene.PickInfos[_hoveredPickId], viewport);
        }

        if (_minimapVisible) DrawMinimap(viewport);
        if (_legendVisible) DrawLegend(viewport);
        if (_worstVisible) DrawWorstList(viewport);
        DrawTourCaption(viewport);
        if (_pickerOpen) DrawPicker(viewport);

        _hud.End();
    }

    /// <summary>
    /// The city's ten worst buildings, ranked, with a distance and a key to fly there.
    /// </summary>
    /// <remarks>
    /// The one panel that isn't a metaphor. Everything else shows a problem where it lives, which is
    /// the right way to read a codebase but a poor way to start work on one — with 1,180 findings
    /// across 1.4 km, "where do I begin?" needs an answer you can act on, not explore for.
    /// </remarks>
    void DrawWorstList(Vector2 viewport)
    {
        const float Pad = 14f;
        const float Title = 17f, Row = 14f, Sub = 11.5f;
        const float LineHeight = 34f;

        float width = 420f;
        float height = Pad * 2f + Title + 10f + _scene.Worst.Count * LineHeight;
        float x = 16f;
        float y = 16f;

        _hud.Rect(x, y, width, height, new Vector4(0.04f, 0.05f, 0.07f, 0.86f));
        _hud.Rect(x, y, 3f, height, new Vector4(1.00f, 0.42f, 0.32f, 1f));
        _hud.Text(x + Pad, y + Pad, Title, new Vector4(1f, 1f, 1f, 1f), "WORST BUILDINGS");

        float cursor = y + Pad + Title + 10f;
        for (int i = 0; i < _scene.Worst.Count; i++)
        {
            var entry = _scene.Worst[i];
            float distance = Vector3.Distance(_camera.Position, entry.Position);

            // The number is the key that flies you there, so it's coloured like a control.
            _hud.Text(x + Pad, cursor, Row, new Vector4(0.98f, 0.86f, 0.45f, 1f),
                $"{(i + 9) % 10 + 1}");
            _hud.Text(x + Pad + 20f, cursor, Row, new Vector4(0.96f, 0.97f, 1f, 1f), entry.Name);

            float far = _hud.Measure($"{distance:F0}m", Sub);
            _hud.Text(x + width - Pad - far, cursor + 2f, Sub,
                new Vector4(0.58f, 0.62f, 0.70f, 1f), $"{distance:F0}m");

            _hud.Text(x + Pad + 20f, cursor + Row + 3f, Sub,
                new Vector4(1.00f, 0.62f, 0.42f, 1f), entry.Reason);

            cursor += LineHeight;
        }
    }

    /// <summary>
    /// Names the tour stop, centred and fading out. Without it you arrive somewhere dramatic with no
    /// idea which building it is or why it's on fire.
    /// </summary>
    /// <summary>
    /// The destination list: type to filter, arrows to choose, Enter to go.
    /// </summary>
    /// <remarks>
    /// The HUD has no scissor rectangle and no notion of clipping, so the list is not clipped — it
    /// is <em>sliced</em>. Only the rows that fall inside the panel are ever submitted, which needs
    /// no clipping to stay inside its box and costs sixteen draw calls whether the city has forty
    /// buildings or four thousand.
    /// </remarks>
    void DrawPicker(Vector2 viewport)
    {
        const float Width = 560f, RowHeight = 22f, Pad = 16f, TitleSize = 17f, RowSize = 13f;
        float height = Pad * 2f + 34f + 30f + (PickerRows + 1) * RowHeight + 24f;
        float x = MathF.Round((viewport.X - Width) * 0.5f);
        float y = MathF.Round((viewport.Y - height) * 0.5f);

        _hud.Rect(x - 2f, y - 2f, Width + 4f, height + 4f, new Vector4(0f, 0f, 0f, 0.55f));
        _hud.Rect(x, y, Width, height, new Vector4(0.07f, 0.08f, 0.11f, 0.95f));
        _hud.Rect(x, y, 3f, height, new Vector4(0.95f, 0.78f, 0.32f, 1f));

        float cursor = y + Pad;
        _hud.Text(x + Pad, cursor, TitleSize, new Vector4(1f, 1f, 1f, 1f), "DRIVE TO...");
        cursor += 30f;

        // Query line, with a caret that blinks so an empty box still reads as "type here".
        _hud.Rect(x + Pad, cursor - 3f, Width - Pad * 2f, 24f, new Vector4(0f, 0f, 0f, 0.4f));
        float queryWidth = _hud.Text(x + Pad + 8f, cursor, RowSize + 1f,
            new Vector4(0.92f, 0.94f, 1f, 1f), _query);
        if ((int)(_elapsed * 2.0) % 2 == 0)
            _hud.Rect(x + Pad + 9f + queryWidth, cursor - 1f, 2f, 18f,
                new Vector4(0.95f, 0.78f, 0.32f, 1f));
        cursor += 30f;

        var dim = new Vector4(0.62f, 0.66f, 0.74f, 1f);
        var bright = new Vector4(1f, 1f, 1f, 1f);

        // Row zero is always the endless option, so it survives any filter.
        DrawPickerRow(x, cursor, Width, RowHeight, selected: _pickerSelection == 0,
            "[ Anywhere - just keep driving ]", "", RowSize, bright, dim);
        cursor += RowHeight;

        for (int row = 0; row < PickerRows; row++)
        {
            int index = _pickerScroll + row;
            if (index >= _matches.Count) break;

            var entry = _destinations[_matches[index]];
            float distance = Vector3.Distance(_camera.Position, entry.Centre);
            DrawPickerRow(x, cursor + row * RowHeight, Width, RowHeight,
                selected: _pickerSelection == index + 1,
                entry.Display, $"{entry.Project}  {distance:F0}m", RowSize, bright, dim);
        }

        // A thumb rather than a bar: it says where you are without pretending to be draggable.
        if (_matches.Count > PickerRows)
        {
            float track = PickerRows * RowHeight;
            float thumb = MathF.Max(18f, track * PickerRows / _matches.Count);
            float travel = (track - thumb) * _pickerScroll / MathF.Max(1, _matches.Count - PickerRows);
            _hud.Rect(x + Width - 8f, cursor + travel, 4f, thumb, new Vector4(0.5f, 0.55f, 0.65f, 1f));
        }

        _hud.Text(x + Pad, y + height - 26f, 12f, dim,
            $"{_matches.Count} building(s)   Up/Dn select   Enter drive   Esc cancel");
    }

    void DrawPickerRow(float x, float y, float width, float height, bool selected, string name,
        string detail, float size, Vector4 bright, Vector4 dim)
    {
        if (selected)
            _hud.Rect(x + 8f, y - 2f, width - 16f, height, new Vector4(0.20f, 0.26f, 0.38f, 1f));

        _hud.Text(x + 18f, y, size, selected ? bright : new Vector4(0.85f, 0.88f, 0.94f, 1f), name);
        if (detail.Length == 0) return;

        float measured = _hud.Measure(detail, size - 1f);
        _hud.Text(x + width - 18f - measured, y + 1f, size - 1f, dim, detail);
    }

    void DrawTourCaption(Vector2 viewport)
    {
        const double Hold = 6.0;
        if (_arrivedAt is null || _captionSeconds > Hold) return;

        float fade = (float)Math.Clamp((Hold - _captionSeconds) / 1.5, 0, 1);
        const float TitleSize = 26f, DetailSize = 15f;

        float width = MathF.Max(_hud.Measure(_arrivedAt.Headline, TitleSize),
                                _hud.Measure(_arrivedAt.Detail, DetailSize)) + 40f;
        float height = 78f;
        float x = (viewport.X - width) * 0.5f;
        float y = viewport.Y * 0.72f;

        _hud.Rect(x, y, width, height, new Vector4(0.03f, 0.04f, 0.06f, 0.80f * fade));
        _hud.Rect(x, y, width, 3f, new Vector4(1.00f, 0.62f, 0.28f, fade));

        _hud.Text(x + 20f, y + 16f, TitleSize, new Vector4(1f, 1f, 1f, fade), _arrivedAt.Headline);
        _hud.Text(x + 20f, y + 48f, DetailSize, new Vector4(0.78f, 0.82f, 0.88f, fade),
            _arrivedAt.Detail);

        int total = _scene.Interest.Count;
        _hud.Text(x + width - 46f, y + 16f, DetailSize,
            new Vector4(0.62f, 0.66f, 0.72f, fade), $"{_tourIndex + 1}/{total}");
    }

    /// <summary>
    /// The key legend, with each toggle's current state. Showing state as well as the binding is the
    /// point — with nine independent layers, "why can't I see the rail" is otherwise a guessing game.
    /// </summary>
    void DrawLegend(Vector2 viewport)
    {
        (string Key, string Name, bool? On)[] rows =
        {
            ("C", "tour next incident", null),
            ("B", "worst buildings (1-0 to fly)", _worstVisible),
            ("R", "drive to a building", _riding),
            ("=", $"fast forward (x{_sim.TimeScale:0})", _sim.TimeScale > 1f),
            ("F1", "this legend", true),
            ("F2", "smog", (_layers & CityLayer.Smog) != 0),
            ("F3", "rail", (_layers & CityLayer.Rail) != 0),
            ("F4", "roundabouts", (_layers & CityLayer.Roundabouts) != 0),
            ("F5", "footpaths", (_layers & CityLayer.Footpaths) != 0),
            ("F9", "people", (_layers & CityLayer.Walkers) != 0),
            ("F6", "highways", (_layers & CityLayer.Highways) != 0),
            ("F7", "airports", (_layers & CityLayer.Air) != 0),
            ("F8", "inspect", _inspectVisible),
            ("F10", "sidewalks", (_layers & CityLayer.Sidewalks) != 0),
            ("L", "labels", _labelsVisible),
            ("T", "traffic", _trafficVisible),
            ("M", "minimap", _minimapVisible),
            ("Tab", "day / night", _nightTarget),
            ("F", "fly", null),
            ("F11", "fullscreen", _window.WindowState == WindowState.Fullscreen),
            ("Shift", "sprint", null),
        };

        const float Pad = 12f;
        const float Size = 13f;
        const float Line = 19f;
        const float KeyColumn = 42f;

        float width = KeyColumn + Pad * 2f + 96f;
        float height = Pad * 2f + rows.Length * Line;
        float x = 16f;
        float y = viewport.Y - height - 16f;

        _hud.Rect(x, y, width, height, new Vector4(0.04f, 0.05f, 0.07f, 0.82f));
        _hud.Rect(x, y, 3f, height, new Vector4(0.42f, 0.68f, 0.95f, 1f));

        float cursor = y + Pad;
        foreach (var (key, name, on) in rows)
        {
            _hud.Text(x + Pad, cursor, Size, new Vector4(0.98f, 0.86f, 0.45f, 1f), key);

            // Unswitchable actions (fly, sprint) read dimmer than an active toggle but not "off".
            var colour = on switch
            {
                true => new Vector4(0.92f, 0.95f, 1.00f, 1f),
                false => new Vector4(0.45f, 0.49f, 0.55f, 1f),
                null => new Vector4(0.70f, 0.74f, 0.80f, 1f),
            };
            _hud.Text(x + Pad + KeyColumn, cursor, Size, colour, name);

            cursor += Line;
        }
    }

    void DrawInspectionCard(PickInfo info, Vector2 viewport)
    {
        const float Pad = 14f;
        const float TitleSize = 19f;
        const float BodySize = 14f;
        const float LineGap = 6f;

        var lines = new List<(string Text, Vector4 Color)>
        {
            ($"{info.Kind} · {info.Loc} LOC · complexity {info.AvgComplexity:F1}",
                new Vector4(0.72f, 0.78f, 0.86f, 1f)),
        };

        if (info.FilePath.Length > 0)
            lines.Add(($"{Path.GetFileName(info.FilePath)}:{info.Line}",
                new Vector4(0.58f, 0.64f, 0.72f, 1f)));

        // Compiler diagnostics first: they're the ones with a fix attached.
        foreach (var diagnostic in info.DiagnosticLabels)
            lines.Add(($"* {diagnostic}", new Vector4(1.00f, 0.48f, 0.42f, 1f)));

        foreach (var smell in info.SmellLabels.Take(6))
            lines.Add(($"! {smell}", new Vector4(1.00f, 0.66f, 0.32f, 1f)));

        float width = _hud.Measure(info.DisplayName, TitleSize);
        foreach (var (text, _) in lines)
            width = MathF.Max(width, _hud.Measure(text, BodySize));
        width += Pad * 2f;

        float height = Pad * 2f + TitleSize + LineGap
                       + lines.Count * (BodySize + LineGap);

        // Below and left of the crosshair, so it never covers what you're aiming at.
        float x = viewport.X * 0.5f + 26f;
        float y = viewport.Y * 0.5f + 18f;
        x = MathF.Min(x, viewport.X - width - 12f);
        y = MathF.Min(y, viewport.Y - height - 12f);

        _hud.Rect(x, y, width, height, new Vector4(0.04f, 0.05f, 0.07f, 0.86f));
        _hud.Rect(x, y, 3f, height, new Vector4(0.42f, 0.68f, 0.95f, 1f));

        float cursor = y + Pad;
        _hud.Text(x + Pad, cursor, TitleSize, new Vector4(1f, 1f, 1f, 1f), info.DisplayName);
        cursor += TitleSize + LineGap;

        foreach (var (text, colour) in lines)
        {
            _hud.Text(x + Pad, cursor, BodySize, colour, text);
            cursor += BodySize + LineGap;
        }
    }

    /// <summary>
    /// Top-down district plan with a heading arrow. With 41 districts spread over 1.4 km, this is
    /// the difference between exploring and being lost.
    /// </summary>
    void DrawMinimap(Vector2 viewport)
    {
        const float Size = 210f;
        const float Margin = 16f;

        float left = viewport.X - Size - Margin;
        float top = Margin;
        var city = _scene.CityBounds;
        float scale = Size / MathF.Max(city.Width, city.Depth);

        _hud.Rect(left - 2f, top - 2f, Size + 4f, Size + 4f, new Vector4(0.55f, 0.62f, 0.72f, 0.55f));
        _hud.Rect(left, top, Size, Size, new Vector4(0.05f, 0.06f, 0.08f, 0.82f));

        foreach (var (_, bounds) in _scene.Districts)
        {
            _hud.Rect(
                left + (bounds.X - city.X) * scale,
                top + (bounds.Z - city.Z) * scale,
                MathF.Max(bounds.Width * scale, 1.5f),
                MathF.Max(bounds.Depth * scale, 1.5f),
                new Vector4(0.30f, 0.34f, 0.40f, 0.95f));
        }

        var you = new Vector2(
            left + (_camera.Position.X - city.X) * scale,
            top + (_camera.Position.Z - city.Z) * scale);

        // A short stub in the facing direction: position alone doesn't tell you which way you're aimed.
        var facing = Vector2.Normalize(new Vector2(_camera.Front.X, _camera.Front.Z));
        for (int i = 1; i <= 7; i++)
        {
            var at = you + facing * i * 1.6f;
            _hud.Rect(at.X - 1f, at.Y - 1f, 2f, 2f, new Vector4(1.00f, 0.85f, 0.35f, 0.85f));
        }

        _hud.Rect(you.X - 3f, you.Y - 3f, 6f, 6f, new Vector4(1.00f, 0.32f, 0.24f, 1f));
    }

    void OnClosing()
    {
        _post.Dispose();
        _sky.Dispose();
        _terrain?.Dispose();
        _hud.Dispose();
        _atlas.Dispose();
        _text.Dispose();
        _traffic.Dispose();
        _cars.Dispose();
        _roads.Dispose();
        _boxes.Dispose();
        _input.Dispose();
    }

    public void Dispose() => _window?.Dispose();
}

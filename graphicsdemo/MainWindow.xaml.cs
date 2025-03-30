using Assimp;

using Microsoft.UI.Xaml;

using SharpGen.Runtime;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D11.Debug;
using Vortice.DXGI;
using Vortice.Mathematics;

using Matrix4x4 = System.Numerics.Matrix4x4;

namespace graphicsdemo;

[StructLayout(LayoutKind.Sequential)]
public struct Vertex
{
    public Vector3 Position;
    public Vector3 Normal;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
struct ConstantBufferData
{
    public Matrix4x4 WorldViewProjection;
    public Matrix4x4 World;
    public Vector4 LightPosition;
}

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer timer;

    private AssimpContext? importer;

    private ID3D11Device? device;
    private ID3D11DeviceContext? deviceContext;
    private IDXGIDevice? dxgiDevice;

    private IDXGISwapChain1? swapChain;

    private ID3D11Texture2D? backBuffer;
    private ID3D11RenderTargetView? renderTargetView;

    private Vortice.WinUI.ISwapChainPanelNative? swapChainPanel;

    private Color4 canvasColor;
    private Viewport viewport;
    private ID3D11PixelShader? pixelShader;
    private ID3D11VertexShader? vertexShader;

    private ID3D11Buffer? vertexBuffer;
    private ID3D11Buffer? indexBuffer;

    private ID3D11InputLayout? inputLayout;

    private readonly uint stride = (uint)sizeof(float) * 3;
    private readonly uint offset = 0;

    private ID3D11Debug? iD3D11Debug;

    private Matrix4x4 worldMatrix;
    private Matrix4x4 projectionMatrix;
    private Matrix4x4 viewMatrix;

    private List<Vertex> vertices;
    private List<uint> indices;
    private ID3D11RasterizerState? rasterizerState;
    private ID3D11DepthStencilState? depthStencilState;
    private ID3D11Buffer constantBuffer;

    private ID3D11DepthStencilView depthStencilView;

    float lightX = 0.0f;
    float lightY = 0.0f;
    float lightZ = 0.0f;

    private bool Stopping = false;
    private bool drawing = false;

    public MainWindow()
    {
        InitializeComponent();

        timer = new();
        timer.Tick += Timer_Tick;
        timer.Interval = TimeSpan.FromMilliseconds(1000 / 60);

        InitializeDirectX();

        unsafe
        {
            stride = (uint)sizeof(Vertex);
            offset = 0;
        }
    }

    private void SwapChainCanvas_Loaded(object sender, RoutedEventArgs e)
    {
        CreateSwapChain();
        LoadModels();
        CreateShaders();
        CreateBuffers();

        SetRenderState();
        timer.Start();
    }

    public void InitializeDirectX()
    {
        canvasColor = new Color4(1.0f, 1.0f, 1.0f, 1.0f);

        FeatureLevel[] featureLevels =
        [
    FeatureLevel.Level_12_1,
    FeatureLevel.Level_12_0,
    FeatureLevel.Level_11_1,
    FeatureLevel.Level_11_0,
    FeatureLevel.Level_10_1,
    FeatureLevel.Level_10_0,
    FeatureLevel.Level_9_3,
    FeatureLevel.Level_9_2,
    FeatureLevel.Level_9_1
        ];

        DeviceCreationFlags creationFlags = DeviceCreationFlags.BgraSupport;
#if DEBUG
        creationFlags |= DeviceCreationFlags.Debug;
#endif

        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            creationFlags,
            featureLevels,
            out ID3D11Device tempDevice,
            out ID3D11DeviceContext tempContext).CheckError();

        device = tempDevice;
        deviceContext = tempContext;

        iD3D11Debug = device.QueryInterfaceOrNull<ID3D11Debug>();

        dxgiDevice = device.QueryInterface<IDXGIDevice>();

    }

    public void CreateSwapChain()
    {
        using (var comObject = new ComObject(SwapChainCanvas))
        {
            swapChainPanel = comObject.QueryInterfaceOrNull<Vortice.WinUI.ISwapChainPanelNative>();
        }

        SwapChainDescription1 swapChainDescription = new()
        {
            Stereo = false,
            Width = (uint)SwapChainCanvas.Width,
            Height = (uint)SwapChainCanvas.Height,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            AlphaMode = AlphaMode.Premultiplied,
            Flags = SwapChainFlags.None,
            SwapEffect = SwapEffect.FlipSequential
        };

        IDXGIAdapter1 dxgiAdapter = dxgiDevice!.GetParent<IDXGIAdapter1>();
        IDXGIFactory2 dxgiFactory = dxgiAdapter.GetParent<IDXGIFactory2>();

        swapChain = dxgiFactory.CreateSwapChainForComposition(device, swapChainDescription);

        dxgiAdapter.Dispose();
        dxgiFactory.Dispose();
        dxgiDevice.Dispose();

        //0 = primary buffer = backbuffer
        backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        renderTargetView = device!.CreateRenderTargetView(backBuffer);


        IDXGISurface dxgiSurface = backBuffer.QueryInterface<IDXGISurface>();
        swapChainPanel?.SetSwapChain(swapChain);

        viewport = new()
        {
            X = 0.0f,
            Y = 0.0f,
            Width = (float)SwapChainCanvas.Width,
            Height = (float)SwapChainCanvas.Height,
            MinDepth = 0.0f,
            MaxDepth = 1.0f
        };

        Texture2DDescription depthBufferDescription = new()
        {
            Width = (uint)SwapChainCanvas.Width,
            Height = (uint)SwapChainCanvas.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D24_UNorm_S8_UInt,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };

        var depthBuffer = device.CreateTexture2D(depthBufferDescription);

        DepthStencilViewDescription depthStencilViewDescription = new()
        {
            Format = depthBufferDescription.Format,
            ViewDimension = DepthStencilViewDimension.Texture2D,
            Flags = DepthStencilViewFlags.None
        };

        depthStencilView = device.CreateDepthStencilView(depthBuffer, depthStencilViewDescription);
    }

    private void Timer_Tick(object? sender, object e)
    {
        if (Stopping)
        {
            drawing = false;
            return;
        }
        drawing = true;
        Update();
        Draw();
        drawing = false;
    }

    private void Update()
    {
        Vector3 lightPosition = new(lightX, lightY, lightZ);
        float angle = 0.05f;
        worldMatrix *= Matrix4x4.CreateRotationY(angle);
        Matrix4x4 worldViewProjection = worldMatrix * (viewMatrix * projectionMatrix);

        ConstantBufferData data = new()
        {
            World = worldMatrix,
            WorldViewProjection = worldViewProjection,
            LightPosition = new(lightPosition, 1)
        };

        deviceContext!.UpdateSubresource(data, constantBuffer);
    }

    private void Draw()
    {
        deviceContext!.ClearDepthStencilView(depthStencilView, DepthStencilClearFlags.Depth, 1.0f, 0); ;
        deviceContext!.OMSetRenderTargets(renderTargetView!, depthStencilView);
        deviceContext.ClearRenderTargetView(renderTargetView, canvasColor);

        deviceContext.DrawIndexed((uint)indices.Count, 0, 0);
        swapChain!.Present(1, PresentFlags.None);
    }

    private void CreateBuffers()
    {
        RasterizerDescription rasterizerDescription = new(CullMode.Back, FillMode.Solid)
        {
            FrontCounterClockwise = true,
            DepthBias = 0,
            DepthBiasClamp = 0.0f,
            SlopeScaledDepthBias = 0.0f,
            DepthClipEnable = true,
            ScissorEnable = false,
            MultisampleEnable = true,
            AntialiasedLineEnable = false
        };

        rasterizerState = device!.CreateRasterizerState(rasterizerDescription);

        DepthStencilDescription depthStencilDescription = new(true, DepthWriteMask.All, ComparisonFunction.LessEqual)
        {
            StencilEnable = true,
            StencilReadMask = 0xFF,
            StencilWriteMask = 0xFF,
            FrontFace = DepthStencilOperationDescription.Default,
            BackFace = DepthStencilOperationDescription.Default,
        };
        depthStencilState = device.CreateDepthStencilState(depthStencilDescription);

        float aspectRatio = (float)SwapChainCanvas.Width / (float)SwapChainCanvas.Height;
        float fov = 90.0f * (float)Math.PI / 180.0f;
        float nearPlane = 0.1f;
        float farPlane = 100.0f;

        projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(fov, aspectRatio, nearPlane, farPlane);

        Vector3 cameraPosition = new(-1.0f, -1.0f, -5.0f);
        Vector3 cameraTarget = new(0.0f, 0.0f, 0.0f);
        Vector3 cameraUp = new(0.0f, 1.0f, 0.0f);
        viewMatrix = Matrix4x4.CreateLookAt(cameraPosition, cameraTarget, cameraUp);

        var constantBufferDescription = new BufferDescription((uint)Marshal.SizeOf<ConstantBufferData>(), BindFlags.ConstantBuffer);
        constantBuffer = device.CreateBuffer(constantBufferDescription);

        deviceContext!.VSSetConstantBuffers(0, [constantBuffer]);

        worldMatrix = Matrix4x4.Identity;
    }

    private void LoadModels()
    {
        importer = new();
        var modelFile = Path.Combine(AppContext.BaseDirectory, "Assets/Monkey.fbx");
        Scene model = importer.ImportFile(modelFile, PostProcessPreset.TargetRealTimeMaximumQuality);

        Mesh mesh = model.Meshes[0];
        vertices = [.. mesh.Vertices.Zip(mesh.Normals).Select(x => new Vertex() {
            Position = new Vector3(x.First.X, x.First.Z, -x.First.Y),
            Normal= new Vector3(x.Second.X,x.Second.Z, -x.Second.Y)
            })];

        var vertexArray = vertices.ToArray();

        unsafe
        {
            var vertexBufferDescription = new BufferDescription()
            {
                Usage = ResourceUsage.Default,
                ByteWidth = (uint)sizeof(Vertex) * (uint)vertexArray.Length,
                BindFlags = BindFlags.VertexBuffer,
                CPUAccessFlags = CpuAccessFlags.None,
            };


            using var dataStreamVertex = DataStream.Create(vertexArray, true, true);
            vertexBuffer = device!.CreateBuffer(vertexBufferDescription, dataStreamVertex);
        }

        indices = [.. mesh.Faces.SelectMany(x => x.Indices.Select(y => (uint)y))];

        var indexAray = indices.ToArray();

        var indexBufferDescription = new BufferDescription()
        {
            Usage = ResourceUsage.Default,
            ByteWidth = sizeof(uint) * (uint)indexAray.Length,
            BindFlags = BindFlags.IndexBuffer,
            CPUAccessFlags = CpuAccessFlags.None,
        };
        using var dataStreamIndex = DataStream.Create(indexAray, true, true);
        indexBuffer = device!.CreateBuffer(indexBufferDescription, dataStreamIndex);
    }

    private void CreateShaders()
    {
        string vertexShaderFile = Path.Combine(AppContext.BaseDirectory, "VertexShader.hlsl");
        string pixelShaderFile = Path.Combine(AppContext.BaseDirectory, "PixelShader.hlsl");

        var vertexEntryPoint = "VS";
        var vertexProfile = "vs_5_0";
        ReadOnlyMemory<byte> vertexShaderByteCode = Compiler.CompileFromFile(vertexShaderFile, vertexEntryPoint, vertexProfile);

        var pixelEntryPoint = "PS";
        var pixelProfile = "ps_5_0";
        ReadOnlyMemory<byte> pixelShaderByteCode = Compiler.CompileFromFile(pixelShaderFile, pixelEntryPoint, pixelProfile);

        vertexShader = device!.CreateVertexShader(vertexShaderByteCode.Span);
        pixelShader = device.CreatePixelShader(pixelShaderByteCode.Span);


        InputElementDescription[] inputElements =
[
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
];
        inputLayout = device.CreateInputLayout(inputElements, vertexShaderByteCode.Span);
    }

    private void SetRenderState()
    {
        deviceContext!.VSSetShader(vertexShader, null, 0);
        deviceContext.PSSetShader(pixelShader, null, 0);
        deviceContext.PSSetConstantBuffer(0, constantBuffer);

        deviceContext.IASetVertexBuffers(
            0,
            [vertexBuffer!],
            [stride],
            [offset]);

        deviceContext.IASetIndexBuffer(indexBuffer, Format.R32_UInt, 0);

        deviceContext.IASetInputLayout(inputLayout);
        inputLayout!.Dispose();
        deviceContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        deviceContext.RSSetViewports([viewport]);

        deviceContext.RSSetState(rasterizerState);
        rasterizerState?.Dispose();

        deviceContext.OMSetDepthStencilState(depthStencilState, 1);
        depthStencilState?.Dispose();
    }

    private void Window_Closed(object sender, WindowEventArgs e)
    {
        Stopping = true;
        while (drawing)
        {             //wait for drawing to stop
        }

        deviceContext?.ClearState();
        deviceContext?.Flush();

        device?.Dispose();
        deviceContext?.Dispose();
        swapChain?.Dispose();
        backBuffer?.Dispose();
        renderTargetView?.Dispose();
        vertexShader?.Dispose();
        pixelShader?.Dispose();
        vertexBuffer?.Dispose();
        indexBuffer?.Dispose();
        swapChainPanel?.Dispose();
    }

    private void SliderX_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        lightX = (float)e.NewValue;
    }
    private void SliderY_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        lightY = (float)e.NewValue;
    }
    private void SliderZ_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        lightZ = (float)e.NewValue;
    }
}

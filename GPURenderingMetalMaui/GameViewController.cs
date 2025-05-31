using System.Runtime.InteropServices;
using System.Numerics;

using Metal;
using MetalKit;

namespace GPURenderingMetal
{
    public static class CGPointEx
    {
        public static CGPoint Add(CGPoint left, CGPoint right)
        {
            return new CGPoint(left.X + right.X, left.Y + right.Y);
        }

        public static CGPoint Sub(CGPoint left, CGPoint right)
        {
            return new CGPoint(left.X - right.X, left.Y - right.Y);
        }

        public static CGPoint Multi(NFloat left, CGPoint right)
        {
            return new CGPoint(left * right.X, left * right.Y);
        }
    }

    [Register("GameViewController")]
    public class GameViewController : UIViewController, IMTKViewDelegate
    {
        /// <summary>
        /// 頂点データの構造体を定義します。
        /// </summary>
        private struct VertexAttribute
        {
            public Vector3 Position;            // 頂点位置
            public Vector2 TextureCoordinate;   // テクスチャ座標
        }

        /// <summary>
        /// 頂点データ以外の構造体を定義します。
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct Uniform
        {
            [FieldOffset(0)]
            public int ViewportSizeX;       // ビューポートサイズX
            [FieldOffset(4)]
            public int ViewportSizeY;       // ビューポートサイズY
            [FieldOffset(16)]
            public Matrix4x4 ModelMatrix;   // モデル行列 16byte目から配置されるように指定
            [FieldOffset(80)]
            public Matrix4x4 ViewMatrix;    // ビュー行列 80byte目から配置されるように指定
        }

        #region 定数データ
        /// <summary>
        /// テクスチャ幅（固定）
        /// </summary>
        private static readonly int TextureWidth = 1184;
        /// <summary>
        /// テクスチャ高さ（固定）
        /// </summary>
        private static readonly int TextureHeight = 740;

        /// <summary>
        /// 四角形のための頂点座標データ（固定）
        /// </summary>
        private static readonly VertexAttribute[] vertexData = {
            new VertexAttribute() { // 左下
                Position = new Vector3(-TextureWidth / 2, -TextureHeight / 2, 0.0f), TextureCoordinate = new Vector2(0.0f, 1.0f)
            },
            new VertexAttribute() { // 左上
                Position = new Vector3(-TextureWidth / 2,  TextureHeight / 2, 0.0f), TextureCoordinate = new Vector2(0.0f, 0.0f)
            },
            new VertexAttribute() { // 右下
                Position = new Vector3(TextureWidth / 2, -TextureHeight / 2, 0.0f), TextureCoordinate = new Vector2(1.0f, 1.0f)
            },
            new VertexAttribute() { // 右上
                Position = new Vector3(TextureWidth / 2,  TextureHeight / 2, 0.0f), TextureCoordinate = new Vector2(1.0f, 0.0f)
            }
        };
        #endregion

        /// <summary>
        /// バッファ番号を定義します。
        /// </summary>
        private enum Buffers
        {
            VertexAttributeIndex = 0,   // 頂点データ
            UniformIndex                // ユニフォームデータ
        }

        /// <summary>
        /// テスクチャ番号を定義します。
        /// </summary>
        private enum Textures
        {
            DisplayTextureInde = 0,     // 表示するテクスチャ
        }

        #region メンバ変数
        /// <summary>
        /// 使用するGPU
        /// </summary>
        private IMTLDevice device;

        /// <summary>
        /// コマンドキュー
        /// </summary>
        private IMTLCommandQueue commandQueue;

        /// <summary>
        /// パイプラインステート
        /// </summary>
        private IMTLRenderPipelineState pipelineState;

        /// <summary>
        /// 頂点データバッファ
        /// </summary>
        private IMTLBuffer vertexBuffer;

        /// <summary>
        /// テクスチャ
        /// </summary>
        private IMTLTexture texture;

        /// <summary>
        /// ユニフォームデータ
        /// </summary>
        private Uniform uniform = new Uniform();

        /// <summary>
        /// ユニフォームデータバッファ
        /// </summary>
        private IMTLBuffer uniformBuffer;

        /// <summary>
        /// 移動量
        /// </summary>
        private CGPoint move = CGPoint.Empty;
        private CGPoint prevPoint = CGPoint.Empty;

        /// <summary>
        /// 拡大率
        /// </summary>
        private nfloat scale = 1.0f;
        private nfloat prevScaled = 1.0f;

        /// <summary>
        /// 回転量
        /// </summary>
        private nfloat rotate = 0.0f;
        private nfloat oldRotated = 0.0f;
        #endregion

        protected GameViewController(IntPtr handle) : base(handle)
        {
        }

        #region ViewController
        /// <summary>
        /// コントローラーのビューがメモリにロードされた後に呼び出されます。
        /// </summary>
        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            View.Opaque = true;
            View.BackgroundColor = null;
            View.ContentScaleFactor = UIScreen.MainScreen.Scale;

            // パンジェスチャー追加
            View.AddGestureRecognizer(new UIPanGestureRecognizer((UIPanGestureRecognizer sender) =>
            {
                if (sender.State == UIGestureRecognizerState.Began)
                {
                    prevPoint = CGPointEx.Multi(View.ContentScaleFactor, sender.TranslationInView(View));
                }
                else if (sender.State == UIGestureRecognizerState.Changed)
                {
                    CGPoint current = CGPointEx.Multi(View.ContentScaleFactor, sender.TranslationInView(View));
                    CGPoint diff = CGPointEx.Sub(current, prevPoint);
                    move = new CGPoint(move.X + diff.X, move.Y - diff.Y);
                    prevPoint = current;
                }
            }));
            // ピンチジェスチャー追加
            View.AddGestureRecognizer(new UIPinchGestureRecognizer((UIPinchGestureRecognizer sender) =>
            {
                if (sender.State == UIGestureRecognizerState.Began)
                {
                    prevScaled = sender.Scale;
                }
                else if (sender.State == UIGestureRecognizerState.Changed)
                {
                    // 画面中心を原点とした座標系での、ピンチの重心座標
                    NFloat viewCenterX = (sender.LocationInView(View).X - View.Frame.Width / 2) * View.ContentScaleFactor;
                    NFloat viewCenterY = (-(sender.LocationInView(View).Y - View.Frame.Height / 2)) * View.ContentScaleFactor;

                    // Metalの描画座標系での、ピンチの重心座標
                    CGPoint centerPoint = new CGPoint(-viewCenterX + move.X, -viewCenterY + move.Y);

                    // 前回とのスケール比
                    nfloat ratio = sender.Scale / prevScaled;

                    scale = scale * ratio;
                    // Metalの描画座標系での、ピンチの重心座標に移動後
                    // ピンチの重心を画面の元の位置に調整する
                    move = CGPointEx.Add(CGPointEx.Multi(ratio, centerPoint), new CGPoint(viewCenterX, viewCenterY));
                    prevScaled = sender.Scale;
                }
            }));
            // ローテーションジェスチャー追加
            View.AddGestureRecognizer(new UIRotationGestureRecognizer((UIRotationGestureRecognizer sender) =>
            {
                if (sender.State == UIGestureRecognizerState.Began)
                {
                    oldRotated = rotate;
                }
                nfloat tmpRotation = sender.Rotation;
                rotate = oldRotated + tmpRotation;
            }));
            // タップジェスチャー追加
            View.AddGestureRecognizer(new UITapGestureRecognizer((UITapGestureRecognizer sender) =>
            {
                NFloat centerX = sender.LocationInView(View).X - View.Frame.Width / 2;
                NFloat centerY = -(sender.LocationInView(View).Y - View.Frame.Height / 2);
                // タップした位置に移動
                move = new CGPoint(move.X - View.ContentScaleFactor * centerX, move.Y - View.ContentScaleFactor * centerY);
            }));

            SetupMetal();
        }

        /// <summary>
        /// アプリがメモリ警告を受信すると、呼び出されます。
        /// </summary>
        public override void DidReceiveMemoryWarning()
        {
            base.DidReceiveMemoryWarning();

            // Dispose of any resources that can be recreated.
        }

        /// <summary>
        /// View Controllerがステータスバーを非表示にするか表示するかを指定します。
        /// </summary>
        /// <returns></returns>
        public override bool PrefersStatusBarHidden()
        {
            return true;
        }
        #endregion

        #region Metal
        /// <summary>
        /// Metalのセットアップを行います。
        /// </summary>
        private void SetupMetal()
        {
            // 使用するGPUの選択
            device = MTLDevice.SystemDefault;

            // Viewの設定
            MTKView view = (MTKView)View;
            view.Device = device;
            view.ColorPixelFormat = MTLPixelFormat.BGRA8Unorm;
            view.Delegate = this;

            // コマンドキューの作成
            commandQueue = device.CreateCommandQueue();

            // シェーダのロード
            LoadShaders();

            // テクスチャのロード
            LoadTexture();

            // 頂点バッファの作成
            vertexBuffer = device.CreateBuffer((nuint)(Marshal.SizeOf(typeof(VertexAttribute)) * vertexData.Length), MTLResourceOptions.CpuCacheModeDefault);
            vertexBuffer.Label = "Vertices";

            // 頂点バッファにデータコピー
            CopyToBuffer(vertexData, vertexBuffer);

            // ユニフォームバッファの作成
            uniformBuffer = device.CreateBuffer((nuint)Marshal.SizeOf(typeof(Uniform)), MTLResourceOptions.CpuCacheModeDefault);
            uniformBuffer.Label = "Uniforms";

            // サイズの指定
            DrawableSizeWillChange(view, view.DrawableSize);
        }

        /// <summary>
        /// テクスチャをロードします。
        /// </summary>
        /// <returns></returns>
        private bool LoadTexture()
        {
            NSUrl url = NSBundle.MainBundle.GetUrlForResource("apple-evolution-thumbnail", "jpg");

            MTKTextureLoader loader = new MTKTextureLoader(device);

            NSError error;
            texture = loader.FromUrl(url, null, out error);

            if (texture == null)
            {
                Console.WriteLine("Failed to load texture, error " + error);
                return false;
            }

            return true;
        }

        /// <summary>
        /// シェーダをロードします。
        /// </summary>
        /// <returns></returns>
        private bool LoadShaders()
        {
            // プロジェクト内のmetal拡張子のシェーダファイルを全て読み込む
            NSError error;
            IMTLLibrary defaultLibrary = device.CreateLibrary("default.metallib", out error);
            if (error != null)
            {
                Console.WriteLine("Failed to created library, error " + error);
                return false;
            }

            // 頂点シェーダの読み込み
            IMTLFunction vertexProgram = defaultLibrary.CreateFunction("sample_vertex");
            // フラグメントシェーダの読み込み
            IMTLFunction fragmentProgram = defaultLibrary.CreateFunction("sample_fragment");

            // パイプラインステートの作成
            MTLRenderPipelineDescriptor pipelineStateDescriptor = new MTLRenderPipelineDescriptor
            {
                Label = "MyPipeline",
                SampleCount = 1,
                VertexFunction = vertexProgram,
                FragmentFunction = fragmentProgram,
            };
            pipelineStateDescriptor.ColorAttachments[0].PixelFormat = MTLPixelFormat.BGRA8Unorm;
            pipelineState = device.CreateRenderPipelineState(pipelineStateDescriptor, out error);

            if (pipelineState == null)
            {
                Console.WriteLine("Failed to created pipeline state, error " + error);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 各フレームが表示される前に呼び出されます。
        /// </summary>
        private void Update()
        {
            Matrix4x4 translationMatrix = Matrix4x4.CreateTranslation(0.0f, 0.0f, 0.0f);
            // UIRotationGestureRecognizerは時計回りが正
            // CreateRotationZは反時計回りが正
            Matrix4x4 rotationMatrix = Matrix4x4.CreateRotationZ((float)-rotate);
            Matrix4x4 scaleMatrix = Matrix4x4.CreateScale((float)scale, (float)scale, 1.0f);

            uniform.ModelMatrix = translationMatrix * rotationMatrix * scaleMatrix;

            uniform.ViewMatrix = Matrix4x4.CreateTranslation((float)move.X, (float)move.Y, 0.0f);

            // データをバッファにコピー
            CopyToBuffer(uniform, uniformBuffer);
        }

        /// <summary>
        /// レイアウト、解像度、またはサイズの変更時に呼ばれます。
        /// </summary>
        /// <param name="view">コンテンツの更新を要求するビュー</param>
        /// <param name="size">ビューの新しい描画可能なサイズ</param>
        public void DrawableSizeWillChange(MTKView view, CGSize size)
        {
            uniform.ViewportSizeX = (int)size.Width;
            uniform.ViewportSizeY = (int)size.Height;
        }

        /// <summary>
        /// 描画処理を行います。
        /// </summary>
        /// <param name="view">コンテンツの再描画を要求するビュー</param>
        public void Draw(MTKView view)
        {
            Update();

            // コマンドバッファを作成
            IMTLCommandBuffer commandBuffer = commandQueue.CommandBuffer();
            commandBuffer.Label = "MyCommand";

            // レンダーパスを取得して、クリア
            MTLRenderPassDescriptor renderPassDescriptor = (View as MTKView).CurrentRenderPassDescriptor;
            renderPassDescriptor.ColorAttachments[0].ClearColor = new MTLClearColor(0.65f, 0.65f, 0.65f, 1.0f);

            // レンダーコマンドエンコーダを作成
            IMTLRenderCommandEncoder renderEncoder = commandBuffer.CreateRenderCommandEncoder(renderPassDescriptor);
            renderEncoder.Label = "MyRenderEncoder";

            // パイプラインステート指定
            renderEncoder.SetRenderPipelineState(pipelineState);

            // 頂点データバッファ指定
            renderEncoder.SetVertexBuffer(vertexBuffer, 0, (uint)Buffers.VertexAttributeIndex);
            // ユニフォームデータバッファ指定
            renderEncoder.SetVertexBuffer(uniformBuffer, 0, (uint)Buffers.UniformIndex);
            // テクスチャ指定
            renderEncoder.SetFragmentTexture(texture, (uint)Textures.DisplayTextureInde);

            // プリミティブを指定
            renderEncoder.DrawPrimitives(MTLPrimitiveType.TriangleStrip, 0, 4);

            // エンコード終了を通知
            renderEncoder.EndEncoding();

            // コマンド完了後の表示をスケジュールする
            commandBuffer.PresentDrawable((View as MTKView).CurrentDrawable);

            // コマンドバッファをGPUに送る
            commandBuffer.Commit();
        }
        #endregion

        /// <summary>
        /// 構造体をバッファにコピーします。
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="st">構造体</param>
        /// <param name="buffer">バッファ</param>
        public static void CopyToBuffer<T>(T st, IMTLBuffer buffer) where T : struct
        {
            // 構造体のサイズ取得
            int rawsize = Marshal.SizeOf(typeof(T));

            // マネージドバイト配列確保
            byte[] rawdata = new byte[rawsize];
            // アンマネージドメモリ確保
            IntPtr ptr = Marshal.AllocHGlobal(rawsize);

            // 構造体データをアンマネージドメモリにコピー
            Marshal.StructureToPtr(st, ptr, false);
            // アンマネージドメモリからバイト配列にコピー
            Marshal.Copy(ptr, rawdata, 0, rawsize);

            // アンマネージドメモリ解放
            Marshal.FreeHGlobal(ptr);

            // バイト配列からバッファにデータをコピー
            Marshal.Copy(rawdata, 0, buffer.Contents, rawsize);
        }

        /// <summary>
        /// 構造体配列をバッファにコピーします。
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="st">構造体配列</param>
        /// <param name="buffer">バッファ</param>
        public static void CopyToBuffer<T>(T[] st, IMTLBuffer buffer) where T : struct
        {
            // 構造体のサイズ取得
            int typesize = Marshal.SizeOf(typeof(T));
            // 全体のサイズ取得
            int rawsize = typesize * st.Length;

            // マネージドバイト配列確保
            byte[] rawdata = new byte[rawsize];
            // アンマネージドメモリ確保
            IntPtr ptr = Marshal.AllocHGlobal(typesize);

            for (int i = 0; i < st.Length; i++)
            {
                // 構造体データをアンマネージドメモリにコピー
                Marshal.StructureToPtr(st[i], ptr, false);
                // アンマネージドメモリからバイト配列にコピー
                Marshal.Copy(ptr, rawdata, typesize * i, typesize);
            }

            // アンマネージドメモリ解放
            Marshal.FreeHGlobal(ptr);

            // バイト配列からバッファにデータをコピー
            Marshal.Copy(rawdata, 0, buffer.Contents, rawsize);
        }
    }
}

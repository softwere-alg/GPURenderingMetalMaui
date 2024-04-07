#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

/// 頂点データの構造体を定義します。
typedef struct
{
    // C#でVector3とVector2を連続したバイト列として、変換した後GPUに渡している
    
    // float3は16byteのため、C#のバイト表現と合わない
    // そのため、12byteのpacked_float3を使用
    packed_float3 Position;             // 頂点位置
    // float2のメモリアライメントは8byteになっていて、C#のバイト表現と合わない
    // そのため、メモリアライメントは4byteのfloat2を使用
    packed_float2 TextureCoordinate;    // テクスチャ座標
} VertexAttribute;

/// 頂点データ以外の構造体を定義します。
typedef struct
{
    int2 ViewportSize;              // ビューポートサイズ
    // float4x4のメモリアライメントは16byteになっていて、metalには他の行列表現がない
    // そのため、C#側でバイト列を作成した際に、モデル行列のメモリアライメントは16byteになるように指定する
    float4x4 ModelMatrix;           // モデル行列
    float4x4 ViewMatrix;            // ビュー行列
} Uniform;

/// 頂点シェーダの出力用(フラグメントシェーダ入力用)の構造体を定義します。
typedef struct {
    float4 position [[position]];   // クリップ空間の頂点座標
    
    float2 textureCoordinate;       // テクスチャ座標
} RasterizerData;

/// バッファ番号を定義します。
typedef enum Buffers
{
    VertexAttributeIndex = 0,   // 頂点データ
    UniformIndex                // ユニフォームデータ
} Buffers;

/// テクスチャ番号を定義します。
typedef enum Textures
{
    DisplayTextureIndex = 0     // 表示するテクスチャ
} Textures;

/// 頂点シェーダ関数
/// - Parameters:
///   - vertexID: 頂点ID
///   - vertices: 頂点データ
///   - uniform: ユニフォームデータ
///
/// - Returns: フラグメントシェーダへの出力
vertex RasterizerData sample_vertex(uint vertexID [[vertex_id]],
                                    constant VertexAttribute* vertices [[buffer(VertexAttributeIndex)]],
                                    constant Uniform& uniform [[buffer(UniformIndex)]])
{
    RasterizerData out;
    
    // 頂点データから頂点座標を取得
    packed_float3 packedPos = vertices[vertexID].Position;
    float3 pixelSpacePosition = float3(packedPos.x, packedPos.y, packedPos.z);
    // 平行移動・回転・スケールを適用するためにモデル行列をかける
    // カメラ位置を変更するためにビュー行列をかける
    pixelSpacePosition = (uniform.ViewMatrix * uniform.ModelMatrix * float4(pixelSpacePosition, 1.0)).xyz;
    
    // float型にキャスト
    float2 viewportSize = float2(uniform.ViewportSize);

    // 頂点座標(ピクセル空間)をビューポートサイズの半分で割って、クリップ空間の座標に変換する
    out.position = float4(0.0, 0.0, 0.0, 1.0);
    out.position.xy = pixelSpacePosition.xy / (viewportSize / 2.0);
    
    // テクスチャ座標を出力に設定
    out.textureCoordinate = vertices[vertexID].TextureCoordinate;
    
    return out;
}

/// フラグメントシェーダ関数
/// - Parameters:
///   - in: 頂点シェーダからの入力
///   - colorTexture: テクスチャ
///
/// - Returns: 色情報
fragment float4 sample_fragment(RasterizerData in [[stage_in]],
                                texture2d<float> colorTexture[[texture(DisplayTextureIndex)]])
{
    // サンプラー作成
    constexpr sampler textureSampler(mag_filter::linear,
                                     min_filter::linear);

    // テクスチャからサンプリング
    const float4 colorSample = colorTexture.sample(textureSampler, in.textureCoordinate);
    
    return colorSample;
}

Shader "Pinball/HDRP/ColliderShader"
{
	Properties
	{
		[HDR]_Color ("Color", Color) = (0.1, 1.0, 0.25, 1.0)
		_FaceOpacity ("Face Opacity", Range(0, 1)) = 0.15
		_WireThickness ("Wire Thickness", Range(0.5, 8.0)) = 1.5
		[Toggle] _DrawOnTop ("Draw On Top", Float) = 0
	}

	SubShader
	{
		Tags
		{
			"RenderPipeline" = "HDRenderPipeline"
			"Queue" = "Transparent+100"
			"RenderType" = "Transparent"
		}

		Pass
		{
			Name "ForwardOnly"
			Tags { "LightMode" = "ForwardOnly" }

			Blend SrcAlpha OneMinusSrcAlpha
			ZWrite Off
			ZTest Always
			Cull Off

			HLSLPROGRAM
			#pragma target 4.5
			#pragma only_renderers d3d11 playstation xboxone xboxseries vulkan switch switch2
			#pragma vertex Vert
			#pragma geometry Geom
			#pragma fragment Frag
			#pragma editor_sync_compilation

			#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
			#include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

			CBUFFER_START(UnityPerMaterial)
				float4 _Color;
				float _FaceOpacity;
				float _WireThickness;
				float _DrawOnTop;
			CBUFFER_END

			struct Attributes
			{
				float3 positionOS : POSITION;
			};

			struct VaryingsToGeom
			{
				float4 positionCS : SV_POSITION;
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float3 barycentric : TEXCOORD0;
			};

			float2 SafeNormalize2(float2 v)
			{
				float len2 = dot(v, v);
				if (len2 <= 1e-10)
				{
					return float2(1.0, 0.0);
				}
				return v * rsqrt(len2);
			}

			VaryingsToGeom Vert(Attributes input)
			{
				VaryingsToGeom output;
				output.positionCS = TransformObjectToHClip(input.positionOS);
				return output;
			}

			[maxvertexcount(3)]
			void Geom(triangle VaryingsToGeom input[3], inout TriangleStream<Varyings> stream)
			{
				float4 p0 = input[0].positionCS;
				float4 p1 = input[1].positionCS;
				float4 p2 = input[2].positionCS;

				float w0 = (abs(p0.w) > 1e-6) ? p0.w : 1e-6;
				float w1 = (abs(p1.w) > 1e-6) ? p1.w : 1e-6;
				float w2 = (abs(p2.w) > 1e-6) ? p2.w : 1e-6;

				float2 n0 = p0.xy / w0;
				float2 n1 = p1.xy / w1;
				float2 n2 = p2.xy / w2;

				float2 e0 = n1 - n0;
				float2 e1 = n2 - n0;
				float area2 = abs(e0.x * e1.y - e0.y * e1.x);
				float minArea2 = 8.0 / max(_ScreenSize.x * _ScreenSize.y, 1.0);

				if (area2 <= minArea2)
				{
					float pixelToNdc = 2.0 / max(_ScreenSize.x, _ScreenSize.y);
					float inflate = pixelToNdc * max(_WireThickness, 1.0) * 1.5;

					float d01 = dot(n1 - n0, n1 - n0);
					float d12 = dot(n2 - n1, n2 - n1);
					float d20 = dot(n0 - n2, n0 - n2);
					float maxLen2 = max(d01, max(d12, d20));

					float2 new0 = n0;
					float2 new1 = n1;
					float2 new2 = n2;

					if (maxLen2 <= 1e-12)
					{
						float2 center = (n0 + n1 + n2) / 3.0;
						new0 = center + float2( inflate, 0.0);
						new1 = center + float2(-inflate, 0.0);
						new2 = center + float2(0.0, inflate);
					}
					else if (d01 >= d12 && d01 >= d20)
					{
						float2 axis = SafeNormalize2(n1 - n0);
						float2 perp = float2(-axis.y, axis.x);
						float2 mid = (n0 + n1) * 0.5;
						new2 = mid + perp * inflate;
					}
					else if (d12 >= d20)
					{
						float2 axis = SafeNormalize2(n2 - n1);
						float2 perp = float2(-axis.y, axis.x);
						float2 mid = (n1 + n2) * 0.5;
						new0 = mid + perp * inflate;
					}
					else
					{
						float2 axis = SafeNormalize2(n0 - n2);
						float2 perp = float2(-axis.y, axis.x);
						float2 mid = (n2 + n0) * 0.5;
						new1 = mid + perp * inflate;
					}

					n0 = new0;
					n1 = new1;
					n2 = new2;

					p0.xy = n0 * w0;
					p1.xy = n1 * w1;
					p2.xy = n2 * w2;
				}

				Varyings output = (Varyings)0;

				output.positionCS = p0;
				output.barycentric = float3(1.0, 0.0, 0.0);
				stream.Append(output);

				output.positionCS = p1;
				output.barycentric = float3(0.0, 1.0, 0.0);
				stream.Append(output);

				output.positionCS = p2;
				output.barycentric = float3(0.0, 0.0, 1.0);
				stream.Append(output);
			}

			bool IsOccluded(float fragDeviceDepth, float sceneDeviceDepth)
			{
				// Compare linearized eye depth so this works consistently for
				// perspective + orthographic cameras and reversed-Z setups.
				float fragEyeDepth = LinearEyeDepth(fragDeviceDepth, _ZBufferParams);
				float sceneEyeDepth = LinearEyeDepth(sceneDeviceDepth, _ZBufferParams);
				return fragEyeDepth > (sceneEyeDepth + 1e-4);
			}

			float4 Frag(Varyings input) : SV_Target
			{
				if (_DrawOnTop < 0.5)
				{
					uint2 pixelCoords = (uint2)(input.positionCS.xy * _RTHandleScale.xy);
					pixelCoords = clamp(pixelCoords, uint2(0, 0), (uint2)_ScreenSize.xy - 1);

					// Compare device-depth values from the same space.
					float fragDeviceDepth = input.positionCS.z / max(input.positionCS.w, 1e-6);
					float sceneDeviceDepth = LoadCameraDepth(pixelCoords);

					if (IsOccluded(fragDeviceDepth, sceneDeviceDepth))
					{
						clip(-1);
					}
				}

				float minBarycentric = min(input.barycentric.x, min(input.barycentric.y, input.barycentric.z));
				float pixelWidth = max(fwidth(minBarycentric), 1e-5);
				float wireMask = 1.0 - smoothstep(0.0, pixelWidth * _WireThickness, minBarycentric);

				float faceAlpha = saturate(_Color.a * _FaceOpacity);
				float wireAlpha = saturate(_Color.a);
				float alpha = lerp(faceAlpha, wireAlpha, wireMask);

				return float4(_Color.rgb, alpha);
			}
			ENDHLSL
		}
	}

	// Fallback for platforms without geometry shaders.
	SubShader
	{
		Tags
		{
			"RenderPipeline" = "HDRenderPipeline"
			"Queue" = "Transparent+100"
			"RenderType" = "Transparent"
		}

		Pass
		{
			Name "ForwardOnlyFallback"
			Tags { "LightMode" = "ForwardOnly" }

			Blend SrcAlpha OneMinusSrcAlpha
			ZWrite Off
			ZTest Always
			Cull Off

			HLSLPROGRAM
			#pragma target 4.5
			#pragma only_renderers d3d11 playstation xboxone xboxseries vulkan switch switch2
			#pragma vertex VertFallback
			#pragma fragment FragFallback
			#pragma editor_sync_compilation

			#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
			#include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

			CBUFFER_START(UnityPerMaterial)
				float4 _Color;
				float _FaceOpacity;
				float _DrawOnTop;
			CBUFFER_END

			struct Attributes
			{
				float3 positionOS : POSITION;
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
			};

			Varyings VertFallback(Attributes input)
			{
				Varyings output;
				output.positionCS = TransformObjectToHClip(input.positionOS);
				return output;
			}

			bool IsOccluded(float fragDeviceDepth, float sceneDeviceDepth)
			{
				// Compare linearized eye depth so this works consistently for
				// perspective + orthographic cameras and reversed-Z setups.
				float fragEyeDepth = LinearEyeDepth(fragDeviceDepth, _ZBufferParams);
				float sceneEyeDepth = LinearEyeDepth(sceneDeviceDepth, _ZBufferParams);
				return fragEyeDepth > (sceneEyeDepth + 1e-4);
			}

			float4 FragFallback(Varyings input) : SV_Target
			{
				if (_DrawOnTop < 0.5)
				{
					uint2 pixelCoords = (uint2)(input.positionCS.xy * _RTHandleScale.xy);
					pixelCoords = clamp(pixelCoords, uint2(0, 0), (uint2)_ScreenSize.xy - 1);

					float fragDeviceDepth = input.positionCS.z / max(input.positionCS.w, 1e-6);
					float sceneDeviceDepth = LoadCameraDepth(pixelCoords);

					if (IsOccluded(fragDeviceDepth, sceneDeviceDepth))
					{
						clip(-1);
					}
				}

				return float4(_Color.rgb, saturate(_Color.a * _FaceOpacity));
			}
			ENDHLSL
		}
	}
}

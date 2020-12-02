using VisualPinball.Unity;

namespace VisualPinball.Unity.Hdrp
{
	public class HDRenderPipeline : IRenderPipeline
	{
		public string Name { get; } = "High Definition Render Pipeline";
		public IMaterialConverter MaterialConverter { get; }

		public HDRenderPipeline()
		{
			MaterialConverter = new HdrpMaterialConverter();
		}
	}
}

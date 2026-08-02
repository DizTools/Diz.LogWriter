using Diz.Core;
using Diz.Core.export;
using Diz.LogWriter.assets;
using Diz.LogWriter.util;
using LightInject;

namespace Diz.LogWriter.services;

public class LogWriterServiceRegistration : ICompositionRoot
{
    public void Compose(IServiceRegistry serviceRegistry)
    {
        serviceRegistry.Register<ISampleAssemblyTextGenerator, SampleAssemblyTextGenerator>();
        serviceRegistry.Register<LogWriterSettings, ISampleAssemblyTextGenerator>(CreateSampleAssemblyFromSettings);

        // region asset exporters. named registrations so GetAllInstances<> picks up every
        // one of them; the service matches via IRegionAssetExporter.CanExport (the AssetType
        // prefix, plus the plain-binary ExportType which has no AssetType), not on the name.
        serviceRegistry.Register<IRegionAssetExporter, BinaryRegionAssetExporter>("bin");
        serviceRegistry.Register<IRegionAssetExporter, GfxRegionAssetExporter>("gfx");
        serviceRegistry.Register<IRegionAssetExporter, BrrRegionAssetExporter>("brr");
    }

    private static ISampleAssemblyTextGenerator CreateSampleAssemblyFromSettings(IServiceFactory factory,
        LogWriterSettings logSettings) =>
        new SampleAssemblyTextGenerator(
            factory.GetInstance<ISampleDataFactory>(),
            logSettings
        );
}
using Diz.Core;
using Diz.Core.export;
using Diz.LogWriter.util;
using LightInject;

namespace Diz.LogWriter.services;

public class LogWriterServiceRegistration : ICompositionRoot
{
    public void Compose(IServiceRegistry serviceRegistry)
    {
        serviceRegistry.Register<ISampleAssemblyTextGenerator, SampleAssemblyTextGenerator>();
        serviceRegistry.Register<LogWriterSettings, ISampleAssemblyTextGenerator>(CreateSampleAssemblyFromSettings);
    }

    private static ISampleAssemblyTextGenerator CreateSampleAssemblyFromSettings(IServiceFactory factory,
        LogWriterSettings logSettings) =>
        new SampleAssemblyTextGenerator(
            factory.GetInstance<ISampleDataFactory>(),
            logSettings
        );
}
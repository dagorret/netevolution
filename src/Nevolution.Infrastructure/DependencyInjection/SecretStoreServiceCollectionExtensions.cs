using Microsoft.Extensions.DependencyInjection;
using Nevolution.Core.Abstractions;
using Nevolution.Infrastructure.Secrets;

namespace Nevolution.Infrastructure.DependencyInjection;

public static class SecretStoreServiceCollectionExtensions
{
    public static IServiceCollection AddNevolutionSecretStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ISecretStore>(_ => SecretStoreFactory.CreateDefault());
        return services;
    }
}

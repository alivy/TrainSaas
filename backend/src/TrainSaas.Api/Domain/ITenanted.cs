namespace TrainSaas.Api.Domain;

/// <summary>
/// Marker interface for tenant-scoped entities. Rows always belong to exactly
/// one institution; <see cref="TrainSaas.Api.Data.AppDbContext"/> applies a
/// global query filter on <see cref="InstitutionId"/> for every entity that
/// implements this interface.
/// </summary>
public interface ITenanted
{
    int InstitutionId { get; set; }
}

using Cuebot.Donations.DonorDrive;
using Cuebot.Donations.Pally;
using Cuebot.Donations.StreamElements;
using Cuebot.Donations.Streamlabs;
using Cuebot.Donations.TipeeeStream;
using Cuebot.Donations.TreatStream;

namespace Cuebot.Donations;

/// <summary>Every donation service of the module, for the Donations page and for starting them together.</summary>
public sealed class DonationServices(
    StreamlabsService streamlabs,
    StreamElementsService streamElements,
    TipeeeStreamService tipeeeStream,
    DonorDriveService donorDrive,
    TreatStreamService treatStream,
    PallyService pally)
{
    public StreamlabsService Streamlabs { get; } = streamlabs;
    public StreamElementsService StreamElements { get; } = streamElements;
    public TipeeeStreamService TipeeeStream { get; } = tipeeeStream;
    public DonorDriveService DonorDrive { get; } = donorDrive;
    public TreatStreamService TreatStream { get; } = treatStream;
    public PallyService Pally { get; } = pally;

    public IReadOnlyList<DonationServiceBase> All => [Streamlabs, StreamElements, TipeeeStream, DonorDrive, TreatStream, Pally];

    /// <summary>The services added after Streamlabs/StreamElements (which the app already starts on their own).</summary>
    public IReadOnlyList<DonationServiceBase> Added => [TipeeeStream, DonorDrive, TreatStream, Pally];

    /// <summary>Auto-connects every service that has a token and auto-connect on. Never throws.</summary>
    public Task StartAsync(CancellationToken ct = default) => Task.WhenAll(All.Select(s => s.StartAsync(ct)));
}

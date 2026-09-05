namespace PocketBridge.Core;

/// <summary>Tracks accepted IDs for one pairing, with a fixed memory bound.</summary>
internal sealed class TransferLedger
{
    private readonly HashSet<Guid> _seen = [];

    public void Accept(string transferId)
    {
        if (!Guid.TryParse(transferId, out var id))
            throw new InvalidDataException("파일의 전송 번호가 올바르지 않습니다.");
        // Compare parsed UUIDs, so a different spelling cannot bypass replay protection.
        if (_seen.Contains(id))
            throw new InvalidDataException("이미 사용한 전송 번호입니다. 새 QR로 다시 연결하세요.");
        if (_seen.Count >= Wire.MaxTransfersPerSession)
            throw new InvalidDataException("한 번의 연결에서는 최대 10,000개 파일을 받을 수 있습니다. 새 QR로 다시 연결하세요.");
        _seen.Add(id);
    }
}

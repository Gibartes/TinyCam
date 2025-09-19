namespace TinyCam.Models
{
    public class RequestDTO
    {
        public record GeneralPostRequest(long ts);
        public record UpgradeKeyRequest(string accessKey, long ts);
        public record DownloadRequest(string name, long ts, bool? attachment);
    }
}

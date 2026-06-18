# HockeyPlanner.Backend

## File storage

ImageKit is used by default when `Storage__Provider` is missing or set to `ImageKit`.
To send new uploads to Timeweb S3/Object Storage, configure:

```env
Storage__Provider=S3
S3__Endpoint=https://s3.twcstorage.ru
S3__Region=ru-1
S3__Bucket=hockeyplanner-staging
S3__AccessKey=...
S3__SecretKey=...
S3__PublicBaseUrl=https://hockeyplanner-staging.s3.twcstorage.ru
S3__ForcePathStyle=false
```

Use `hockeyplanner-prod` and the matching public base URL for production.
If Timeweb requires path-style addressing, set `S3__ForcePathStyle=true`.

Uploads continue to go through the backend. Browser direct uploads and presigned URLs are not used.

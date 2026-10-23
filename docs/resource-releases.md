# Resource releases

The resource server can load a validated release and serve its catalog, encoded master tables, and bundles.
The client obtains these files through its normal resource update requests. Resource bytes are served unchanged;
bundle-provider encryption and audio encoding are handled by the packaging tools.

## Build from a song package

Create a song package with [nnnotes](https://github.com/MetaSekaiLab/nnnotes) 0.3.1 or later using `pack build`.
The target must use bundle URLs routed to this server's `/asset/Android/` endpoint. The complete baseline and
decoded master tables are local inputs. A baseline directory has this layout:

```text
baseline/
  Android/catalog_example.bin
  master/base-version/MasterManifest.json
  master/base-version/Master*.bin
```

The baseline contains exactly one master version. `MASTER_JSON` contains the complete decoded tables for the
resulting package, including `MasterPlayerData`, `MasterMemberCard`, `MasterSupportCard`, `MasterLiveMusic`,
`MasterLiveMusicScore`, and `MasterLiveMusicComboReward`.

```sh
dotnet run -- resource-release baseline song-package master-json catalog_example resource-v1 releases/song-v1
```

The command verifies package file sizes and SHA-256 hashes, checks the baseline catalog, combines unchanged and
updated master files, preserves baseline Android assets, and creates `release.json`. It validates the complete master manifest and catalog marker
before making the output directory available. A new output directory is required. An interrupted build can leave
a staging directory for inspection.

Set `Resources:ReleaseManifest` to the resulting `release.json`, then restart the server. Validation finishes
before the server accepts requests. Treat activated files as immutable; build another directory for an update.
The active directory should be read-only to the server process and other applications. Startup validation does
not protect files subsequently changed by another process.

## Release manifest

Paths in `release.json` are relative to that file's directory:

```json
{
  "format": "mynotes.resource-release/1",
  "version": "master-v1",
  "resourceVersion": "resource-v1",
  "catalogName": "catalog_example",
  "masterJsonDirectory": "master-json",
  "files": [
    {"path": "Android/catalog_example.bin", "size": 123, "sha256": "<file SHA-256>"}
  ]
}
```

The `files` list covers every served file. The required catalog files are `Android/<catalogName>.bin` and
`Android/<catalogName>.hash`; the marker is the first 32 lowercase hexadecimal characters of the catalog's
SHA-256. The release contains `master/<version>/MasterManifest.json` and every file that manifest lists, with
matching names, sizes, and hashes. `masterJsonDirectory` is optional for a manually prepared release; when
provided, its six initialization tables must be listed and parse as `_allData` arrays.

The version RPC reports `version` and `resourceVersion` from this manifest. Initialization reads the listed JSON
directory. Song difficulties resolve through `_easyID`, `_normalID`, `_hardID`, and `_expertID` references.
Configured empty tables produce empty collections.

## Download paths

| Request | Content |
| --- | --- |
| `/MasterDataSystemVersion.txt` | Active master version |
| `/Master/MasterDataSystemVersion.txt` | Active master version |
| `/master/<version>/MasterManifest.json` | Complete master manifest |
| `/master/<version>/<file>` | Listed encoded master file |
| `/asset/Android/<catalogName>.bin` | Catalog bytes |
| `/asset/Android/<catalogName>.hash` | Matching catalog marker |
| `/asset/Android/<catalogName>` | Catalog bytes alias |
| `/asset/Android/<file>` or `/Android/<file>` | Bundle or other Android resource |

File endpoints support GET, HEAD, and byte ranges. HEAD reports the actual length without a body. Unsatisfiable
ranges return 416. A release serves only its registered master version.

Android requests resolve in this order: release files, `Static:LocalDirectory/Android/`, the download cache, then
the upstream when `Static:UpstreamEnabled` is true. `Static:LocalDirectory` works with caching disabled. Set
`UpstreamEnabled` to false for an offline mirror. Missing files return 404 in that mode.

Cold upstream range requests forward `Range` and `If-Range` immediately. Complete successful responses can
populate the cache through a temporary file and atomic move. Catalog markers bypass the download cache.
`Static:OfficialAssetRequestTimeoutSeconds` bounds upstream headers and body transfer. An upstream timeout
returns 504 before response streaming begins, or ends the connection once streaming has begun.

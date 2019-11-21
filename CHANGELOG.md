# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.1] - 2019-11-22
### Fixed
- Calculate correct tangents if UVs are present (fixes normal mapping)

## [1.1.0] - 2019-11-21
### Changed
- Assume Draco mesh to be right-handed Y-up coordinates and convert the to Unity's left-handed Y-up by flipping the Z-axis.
- Unity 2018.2 backwards compatibility
### Fixed
- Reference assembly definition by name instead of GUID to avoid package import errors

## [1.0.1] - 2019-09-15
### Changed
- Updated Draco native library binaries
- iOS library is now ~15 MB instead of over 130 MB

## [1.0.0] - 2019-07-28
### Changed
- Recompiled dracodec_unity library with BUILD_FOR_GLTF flag set to true, which adds support for normal encoding and standard edge breaking.
- Opened up interface a bit, which enables custom mesh loading by users.

## [0.9.0] - 2019-07-11
- Initial release

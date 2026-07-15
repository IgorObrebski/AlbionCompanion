# Third-party code: PhotonPackageParser / Protocol16

The files in this `Vendor/Photon16` directory are vendored (copied and modified) from
[`0blu/PhotonPackageParser`](https://github.com/0blu/PhotonPackageParser), MIT licensed.

## Why vendored instead of referenced as a NuGet package

The upstream `Protocol16Deserializer.Deserialize` throws `ArgumentException` for any
Photon parameter type code it doesn't recognize - which includes every game-specific
"custom type" a Photon application (like Albion Online) registers on top of the base
protocol. Since a single unrecognized parameter inside an event's parameter table
aborted deserialization of the *entire* event, real Albion Online traffic (which uses
at least one such custom type extensively) produced zero usable output.

[`ao-data/photon-spectator`](https://github.com/ao-data/photon-spectator) - the Photon
decoder actually used in production by `albiondata-client` - handles this by returning
an error placeholder for the one unrecognized parameter and continuing to decode the
rest of the event. `Protocol16Deserializer.cs` here is patched the same way: the
`default` cases in `Deserialize` and `GetTypeOfCode` no longer throw.

## License

```
MIT License

Copyright (c) 2018 _BLU

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

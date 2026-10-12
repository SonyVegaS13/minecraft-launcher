# Solaris Forge installer — local adaptation

Upstream: https://github.com/CmlLib/CmlLib.Core.Installer.Forge
License: MIT. Original copyright 2023 CmlLib. See LICENSE.

This local project preserves the Forge installation functionality while
avoiding unsolicited browser redirects after installation and hiding the
installer Java worker's console window. It is built as a separate project
to keep the dependency easy to audit and replace.
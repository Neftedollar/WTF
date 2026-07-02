# RPM spec for COPR (Fedora) / OBS (openSUSE). Builds WTF from a tagged
# release with the same scripts CI validates. wlroots + scenefx are vendored
# (pinned) into /usr/lib/wtf — no wlroots dependency on the host.
Name:           wtf-wm
Version:        0.1.0
Release:        1%{?dist}
Summary:        Wayland tiling window manager configured in F#
License:        MIT
URL:            https://github.com/Neftedollar/WTF
Source0:        %{url}/archive/v%{version}/WTF-%{version}.tar.gz

BuildRequires:  git meson ninja-build gcc pkgconf-pkg-config scdoc curl
BuildRequires:  wayland-devel wayland-protocols-devel libxkbcommon-devel
BuildRequires:  libdrm-devel mesa-libgbm-devel pixman-devel
BuildRequires:  libinput-devel libseat-devel systemd-devel
BuildRequires:  mesa-libEGL-devel mesa-libGLES-devel
BuildRequires:  hwdata libdisplay-info-devel libliftoff-devel
BuildRequires:  libxcb-devel xcb-util-renderutil-devel xcb-util-wm-devel
Requires:       libinput libseat libxkbcommon libdrm mesa-libgbm pixman libxcb
Recommends:     xorg-x11-server-Xwayland xdg-desktop-portal-wlr grim libheif

%description
WTF (Wayland Tiling, F#) is a tiling Wayland compositor in the xMonad
tradition: the configuration is real F# code with autocomplete and
hot-reload. scenefx effects (blur, rounded corners, shadows), dynamic .heic
wallpapers, and an agent-first JSON control socket.

%prep
%autosetup -n WTF-%{version}

%build
WTF_ALLOW_ROOT=1 WTF_STAGE_ONLY=1 WTF_PREFIX=/usr bash scripts/install.sh

%install
cp -a build/stage/. %{buildroot}/

%files
/usr/bin/*
/usr/lib/wtf
/usr/share/wtf
/usr/share/wayland-sessions/wtf.desktop
/usr/share/xdg-desktop-portal/wtf-portals.conf

%changelog
%autochangelog

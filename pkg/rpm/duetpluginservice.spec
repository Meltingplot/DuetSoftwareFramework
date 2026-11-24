%define __objdump /usr/bin/true
%define __strip /usr/bin/true
%define _build_id_links none
%global _debug 			0

%global debug_package %{nil}

%global _bindir /usr/bin
%global _datadir /usr/share
%global dsfoptdir /opt/dsf

Name:    duetpluginservice
Version: %{_tversion}
Release: %{_release}
Summary: DSF Plugin Service
Group:   3D Printing
Source0: duetpluginservice_%{_tversion}
License: GPLv3
URL:     https://github.com/Duet3D/DuetSoftwareFramework
BuildRequires: rpm >= 4.7.2-2
Requires: duetcontrolserver = %{_tversion}
%systemd_requires

AutoReq:  0

%description
DSF Service for third-party plugins

%build
mkdir %{buildroot}/%{dsfoptdir}/plugins >/dev/null 2>&1 || :

%pre
if [ $1 -gt 1 ] && systemctl -q is-active %{name}.service ; then
# upgrade
	systemctl stop %{name}.service > /dev/null 2>&1 || :
	systemctl stop %{name}-root.service > /dev/null 2>&1 || :
fi


%post
if [ $1 -gt 1 ] ; then
# upgrade. Re-apply the [Install] section of enabled DPS instances so that new WantedBy= entries take effect
	systemctl -q is-enabled %{name}.service && systemctl -q --no-reload reenable %{name}.service >/dev/null 2>&1 || :
	systemctl -q is-enabled %{name}-root.service && systemctl -q --no-reload reenable %{name}-root.service >/dev/null 2>&1 || :
fi
systemctl daemon-reload >/dev/null 2>&1 || :
# Drop existing per-plugin AppArmor profiles so DPS regenerates them from the current template on its next start
rm -f /etc/apparmor.d/dsf.* >/dev/null 2>&1 || :
# The root service runs the commands configured in plugins.json and loads AppArmor profiles made from apparmor.conf, so the dsf user must not
# be able to change them. Older packages gave both files to dsf, and a changed config file is kept on upgrade. chown -h changes a symlink
# itself instead of its target, and afterwards dsf can no longer replace the entry, so chmod does not follow one either. A symlink is left in
# place, the root service refuses to run commands from it and says so in its log
for file in %{dsfoptdir}/conf/plugins.json %{dsfoptdir}/conf/apparmor.conf ; do
	if [ -e "$file" ] || [ -L "$file" ] ; then
		chown -h root:root "$file"
		[ -L "$file" ] || chmod 644 "$file"
	fi
done

%preun
if [ $1 -eq 0 ] ; then
# remove
	systemctl --no-reload disable %{name}.service >/dev/null 2>&1 || :
	systemctl --no-reload disable %{name}-root.service >/dev/null 2>&1 || :
fi

%postun
if [ $1 -eq 1 ] && systemctl -q is-enabled %{name}.service ; then
# upgrade. Ignore the return code so a failed start does not error the scriptlet
	systemctl start %{name}.service || :
fi
if [ $1 -eq 1 ] && systemctl -q is-enabled %{name}-root.service ; then
	systemctl start %{name}-root.service || :
fi
# On full removal, unload per-plugin AppArmor profiles from the kernel and delete the profile files
if [ $1 -eq 0 ] ; then
	for profile in /etc/apparmor.d/dsf.* ; do
		[ -e "$profile" ] || continue
		apparmor_parser -R "$profile" >/dev/null 2>&1 || :
		rm -f "$profile"
	done
fi

%files
%defattr(0644,root,root,-) 
%{_unitdir}/duetpluginservice.service
%{_unitdir}/duetpluginservice-root.service

%defattr(-,dsf,dsf,-)
%dir %{dsfoptdir}/plugins

# The root service runs these and the commands configured in plugins.json, so the dsf user must not be able to change them
%defattr(-,root,root,-)
%{dsfoptdir}/bin/DuetPluginService
%{dsfoptdir}/bin/DuetPluginService.*
%attr(0755,root,root) %{dsfoptdir}/bin/pipInstall2.py
%config(noreplace) %attr(0644,root,root) %{dsfoptdir}/conf/plugins.json
%config(noreplace) %attr(0644,root,root) %{dsfoptdir}/conf/apparmor.conf



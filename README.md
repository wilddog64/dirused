# dirused

A Windows disk usage and purge tool: from one node, measure local drives and UNC paths across many machines, largest
files first, and free space safely. Private; plan and milestones live in k3d-manager
`docs/plans/dirused-windows-disk-usage-tool.md` until they move here.

## Development VM

Windows 11 ARM64 on VirtualBox (M4), provisioned by Ansible, with a second disk as `D:`.

    make vm-up        # first run: creates .venv, installs collections, boots and provisions
    make vm-check     # OS, .NET SDK, Git, volumes
    make vm-provision # rerun the playbook
    make vm-halt

Pinned: box `stromweld/windows-11` 202511.21.0 (virtualbox, arm64), .NET SDK 10.0.404 (LTS), Git 2.56.1,
PowerShell 7.6.10, ansible-core 2.22.0, pywinrm 0.5.0, ansible.windows 3.9.2, chocolatey.chocolatey 1.6.0.

The VM is for running and looking at the tool. Builds and tests also run on the `windows-latest` CI runner, which is
the offline gate. Multi-node UNC and purge testing needs real nodes (ACG); see the plan's CLI gate.

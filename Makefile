SHELL := /bin/bash
VENV := .venv
PY ?= python3
export PATH := $(CURDIR)/$(VENV)/bin:$(PATH)
export ANSIBLE_CONFIG := $(CURDIR)/ansible.cfg

.PHONY: help venv collections vm-up vm-provision vm-check vm-sync vm-build vm-test vm-live vm-halt vm-destroy

help:
	@echo "venv          create .venv with pinned ansible-core and pywinrm"
	@echo "collections   install pinned Ansible collections into ./collections"
	@echo "vm-up         vagrant up the Windows dev VM (provisions on first boot)"
	@echo "vm-provision  rerun the Ansible playbook"
	@echo "vm-check      print OS, tool versions and volumes from the VM"
	@echo "vm-sync       copy the working tree (tracked + untracked, not ignored) to C:\\src\\dirused"
	@echo "vm-build      vm-sync, then dotnet build -c Release on the VM"
	@echo "vm-test       vm-sync, then dotnet test -c Release on the VM"
	@echo "vm-live       vm-build, then run tools/vm-live.ps1 live checks; evidence in .vagrant/live-evidence.md"
	@echo "vm-halt       stop the VM"
	@echo "vm-destroy    delete the VM and its D: disk"

$(VENV)/bin/ansible-playbook: requirements-dev.txt
	$(PY) -m venv $(VENV)
	$(VENV)/bin/pip install --quiet --upgrade pip
	$(VENV)/bin/pip install --quiet -r requirements-dev.txt
	@touch $@

venv: $(VENV)/bin/ansible-playbook

collections: venv ansible/requirements.yml
	$(VENV)/bin/ansible-galaxy collection install -r ansible/requirements.yml -p ./collections

vm-up: collections
	vagrant up --provider virtualbox

vm-provision: collections
	vagrant provision

vm-check:
	vagrant winrm -s powershell -c '$$env:Path=[Environment]::GetEnvironmentVariable("Path","Machine"); (Get-CimInstance Win32_OperatingSystem).Caption; $$env:PROCESSOR_ARCHITECTURE; dotnet --version; git --version; Get-Volume | Where-Object DriveLetter | Format-Table DriveLetter,FileSystem,Size -AutoSize'

INVENTORY := .vagrant/provisioners/ansible/inventory/vagrant_ansible_inventory

.vagrant/src.zip: FORCE
	@rm -f $@
	@git ls-files -z -co --exclude-standard | xargs -0 zip -q $@

vm-sync vm-build vm-test: collections .vagrant/src.zip
	@[ -f $(INVENTORY) ] || { echo "no Vagrant inventory: run make vm-up first" >&2; exit 2; }
	$(VENV)/bin/ansible-playbook -i $(INVENTORY) ansible/build.yml -e dotnet_step=$(if $(filter vm-sync,$@),none,$(patsubst vm-%,%,$@))

LIVE_EVIDENCE := .vagrant/live-evidence.md

vm-live: vm-build
	@set -o pipefail; \
	sha="$$(git rev-parse --short HEAD)$$(git diff --quiet HEAD -- || echo -dirty)"; \
	vagrant winrm -s powershell -c '& C:\src\dirused\tools\vm-live.ps1; exit $$LASTEXITCODE' | tee .vagrant/live-body.txt; rc=$$?; \
	{ echo "artifact: dirused.exe built by make vm-build at $$sha"; \
	  echo "environment: dirused-dev Vagrant VM ($$(vagrant status --machine-readable 2>/dev/null | awk -F, '$$3=="provider-name"{print $$4}')), $$(date -u +%Y-%m-%d)"; \
	  echo "command: make vm-live (tools/vm-live.ps1)"; \
	  echo "rc: $$rc"; echo "---"; cat .vagrant/live-body.txt; } > $(LIVE_EVIDENCE); \
	echo "evidence: $(LIVE_EVIDENCE)"; exit $$rc

FORCE:

vm-halt:
	vagrant halt

vm-destroy:
	vagrant destroy -f
	rm -f .vagrant/d_drive.vdi

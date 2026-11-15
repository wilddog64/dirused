SHELL := /bin/bash
VENV := .venv
PY ?= python3
export PATH := $(CURDIR)/$(VENV)/bin:$(PATH)
export ANSIBLE_CONFIG := $(CURDIR)/ansible.cfg

.PHONY: help venv collections vm-up vm-provision vm-check vm-halt vm-destroy

help:
	@echo "venv          create .venv with pinned ansible-core and pywinrm"
	@echo "collections   install pinned Ansible collections into ./collections"
	@echo "vm-up         vagrant up the Windows dev VM (provisions on first boot)"
	@echo "vm-provision  rerun the Ansible playbook"
	@echo "vm-check      print OS, tool versions and volumes from the VM"
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

vm-halt:
	vagrant halt

vm-destroy:
	vagrant destroy -f
	rm -f .vagrant/d_drive.vdi

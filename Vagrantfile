# -*- mode: ruby -*-
# vi: set ft=ruby :

BOX_NAME = "stromweld/windows-11"
BOX_VERSION = "202511.21.0"
VM_NAME = "dirused-dev"
D_DRIVE_GB = 40

Vagrant.configure("2") do |config|
  config.vm.box = BOX_NAME
  config.vm.box_version = BOX_VERSION
  config.vm.box_architecture = "arm64"
  config.vm.communicator = "winrm"
  config.vm.boot_timeout = 900

  config.winrm.username = "vagrant"
  config.winrm.password = "vagrant"
  config.winrm.transport = :plaintext
  config.winrm.basic_auth_only = true

  config.vm.synced_folder ".", "/vagrant", disabled: true

  config.vm.provider "virtualbox" do |vb|
    vb.name = VM_NAME
    vb.memory = 6144
    vb.cpus = 4
    vb.gui = false

    disk_path = File.join(__dir__, ".vagrant", "d_drive.vdi")
    unless File.exist?(disk_path)
      vb.customize ["createmedium", "disk", "--filename", disk_path, "--size", D_DRIVE_GB * 1024, "--format", "VDI"]
    end
    vb.customize ["storageattach", :id, "--storagectl", "SATA Controller", "--port", 1, "--device", 0, "--type", "hdd", "--medium", disk_path]
  end

  config.vm.provision "ansible" do |ansible|
    ansible.playbook = "ansible/dev.yml"
    ansible.compatibility_mode = "2.0"
    ansible.host_vars = {
      "default" => {
        "ansible_winrm_scheme" => "http",
        "ansible_winrm_transport" => "basic",
        "ansible_winrm_server_cert_validation" => "ignore",
        "ansible_winrm_operation_timeout_sec" => 120,
        "ansible_winrm_read_timeout_sec" => 150
      }
    }
  end
end

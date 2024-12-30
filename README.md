# Kubeploy2
## What is Kubeploy2?
**Kubeploy2** is a Kubernetes deployment assistant tool for .NET Aspire.
## Usage
```shell
$ kubeploy2 init TestProject -p testproject -k myKubeconfig -c mycluster -r myregs.s.secuaos.work -n mynamespace --imagesecret myreg-secret -s nfs-client -h TestProject.AppHost
$ kubeploy2 prepare
$ kubeploy2 inject
$ kubeploy2 check
$ kubeploy2 deploy --no-wait

$ kubeploy2 undeploy
```
## Use case
https://github.com/Smallbasic-n/NITNC_D1_BOT

// See https://aka.ms/new-console-template for more information

using System.Collections.Specialized;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Dynamic;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using k8s;
using k8s.KubeConfigModels;
using k8s.Models;
using kubeploy2.Manifest;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NJsonSchema;
using NJsonSchema.CodeGeneration.CSharp;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Spec = kubeploy2.Manifest.Spec;

//Console.WriteLine("Hello, World!");
const string MANIFEST_FILE = "./manifest.yaml";
const string ASPIRE_MANIFEST = "tempnifest.json";
var aspireManifest = Path.Combine(Environment.CurrentDirectory, ASPIRE_MANIFEST);
var rootCommand = new RootCommand("dotnet Aspire アプリケーションをかんたんにKubernetesにデプロイします．");
var globalOption = new Option<bool>("--dry-run", "変更されるファイルを列挙し，実際に変更は行いません．");
rootCommand.AddGlobalOption(globalOption);

var initCommand = new Command("init", "ソリューションにkubeploy2を設定します．");
var prefixOpt = new Option<string>(["-p", "--prefix"], "デプロイするリソースすべての接頭辞");
initCommand.AddOption(prefixOpt);
var kubeconfigOpt = new Option<string?>(["-k", "--kubeconfig"], "デプロイに使用するkubeconfig．指定がない場合は~/.kube/config");
initCommand.AddOption(kubeconfigOpt);
var clusterOpt = new Option<string?>(["-c", "--context"], "デプロイに使用するcontext．指定がない場合はdefault");
initCommand.AddOption(clusterOpt);
var registryOpt = new Option<string>(["-r", "--regrepo"], "OCIイメージレジストリ．例：ghcr.io/smallbasic-n");
initCommand.AddOption(registryOpt);
var nameSpaceOpt = new Option<string>(["-n", "--namespace"], "名前空間を指定します．");
initCommand.AddOption(nameSpaceOpt);
var pullSecretOpt = new Option<string?>(["--imagesecret"], "プライベートレジストリなどにアクセスするためのクレデンシャル．指定がない場合は特に考慮しません．");
initCommand.AddOption(pullSecretOpt);
var storageClassOpt = new Option<string?>(["-s","--storage"], "Storage Class．指定がない場合はデフォルトのStorage Classが使用されます．");
initCommand.AddOption(storageClassOpt);
var appHostProjOpt = new Option<string>(["-h","--apphost"], "dotnet AspireのAppHostプロジェクト名を指定します．");
initCommand.AddOption(appHostProjOpt);
var forceOpt = new Option<bool>("--force", "既に"+MANIFEST_FILE+"が存在しても上書きをします．");
initCommand.AddOption(forceOpt);
var appNameArg = new Argument<string>("name", "アプリケーション名");
initCommand.AddArgument(appNameArg);
initCommand.SetHandler(InitCommandHandler);
void InitCommandHandler(InvocationContext obj)
{
    var root = new Root();
    root.Setting = new Setting();
    root.Setting.Prefix = obj.ParseResult.GetValueForOption(prefixOpt)??"";
    if (root.Setting.Prefix == "") {Console.WriteLine("--prefixを指定してください．"); return; }
    root.Setting.Kubeconfig = obj.ParseResult.GetValueForOption(kubeconfigOpt) ?? "~/.kube/config";
    root.Setting.StorageClass = obj.ParseResult.GetValueForOption(storageClassOpt)??"default";
    root.Setting.Context = obj.ParseResult.GetValueForOption(clusterOpt)??"default";
    root.Setting.Registry = obj.ParseResult.GetValueForOption(registryOpt)??"";
    if (root.Setting.Registry == "") {Console.WriteLine("--registryを指定してください．"); return; }
    root.Setting.Namespace = obj.ParseResult.GetValueForOption(nameSpaceOpt)??"";
    if (root.Setting.Namespace == "") {Console.WriteLine("--namespaceを指定してください．"); return; }
    root.Setting.ApplicationName = obj.ParseResult.GetValueForArgument(appNameArg);
    root.Setting.ImagePullSecret = obj.ParseResult.GetValueForOption(pullSecretOpt)??"";
    root.Setting.AppHostProject = obj.ParseResult.GetValueForOption(appHostProjOpt)??"";
    if (root.Setting.AppHostProject == "") {Console.WriteLine("--apphostを指定してください．"); return; }
    var serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();
    var yaml = serializer.Serialize(root);
    if (obj.ParseResult.GetValueForOption(globalOption))
    {
        Console.WriteLine(MANIFEST_FILE+" に次の内容を書き込む予定です．");
        Console.WriteLine(yaml);
    }
    else if (File.Exists(MANIFEST_FILE) && !obj.ParseResult.GetValueForOption(forceOpt))
    {
        Console.WriteLine("既に"+MANIFEST_FILE+"が存在します．強制的に上書きする場合は--forceオプションをつけてください．");
    }
    else
    {
        File.WriteAllTextAsync(MANIFEST_FILE ,yaml);
        Console.WriteLine(MANIFEST_FILE+"を作成しました．");
    }
}
rootCommand.AddCommand(initCommand);

var prepareCommand = new Command("prepare", "kubeploy2でデプロイするアプリケーションの詳細を"+MANIFEST_FILE+"に登録をします．");
prepareCommand.SetHandler(PrepareCommandHandler);
void PrepareCommandHandler(InvocationContext obj)
{
    var manifest = ReadManifest().Item1;

    Console.WriteLine("Aspire Manifestを生成します．");
    var arg =new []{
        "run", "--project", "./" + manifest.Setting.AppHostProject + "/.", "--", "--publisher", "manifest",
        "--output-path", aspireManifest
    };
    Process.Start(new ProcessStartInfo("dotnet", string.Join(" ", arg)))?.WaitForExit();

    var fasts=JsonNode.Parse(File.ReadAllText(aspireManifest)??"")?["resources"]; 
    if (fasts == null)
    {
        Console.WriteLine("不正なAspire Manifestファイルを取得しました．");
        return;
    }
    Console.WriteLine("Aspire Manifestを解析しています．");
    var deserializedObject = JsonConvert.DeserializeObject<ExpandoObject>(fasts.ToJsonString(), new ExpandoObjectConverter());
    var yaml = new YamlStream();
    var rawYaml = new Serializer().Serialize(deserializedObject);
    yaml.Load(new StringReader(rawYaml));
    var aspireManifestData = (YamlMappingNode)yaml.Documents[0].RootNode;
    
    var count = -1;
    while (count!=0)
    {
        var replaced=RawManifest(rawYaml,aspireManifestData);
        count=replaced.Item2;
        rawYaml=replaced.Item1;
        var yaml2=new YamlStream();
        yaml2.Load(new StringReader(rawYaml));
        aspireManifestData=(YamlMappingNode)yaml2.Documents[0].RootNode;
    }

    var manifests = new List<Manifest>();
    if (Equals(manifest.Spec, null)) manifest.Spec = new Spec();
    if (Equals(manifest.Spec.Manifests,null)) manifest.Spec.Manifests = manifests.ToArray();
    foreach (var child in aspireManifestData.Children)
    {
        var node1 = ((YamlMappingNode)child.Value);
        var type = node1["type"].ToString();
        var isProject = type.Contains("project");
        var isContainer = type.Contains("container");
        if (!isProject && !isContainer) continue;

        var history = new List<History>().ToArray();
        if (manifest.Spec.Manifests.Any(x => child.Key.ToString() == x.ServiceName))
        {
            history = manifest.Spec.Manifests.Single(x => child.Key.ToString() == x.ServiceName).History;
        }
        var envs=new List<EnvironmentVar>();
        if (node1["env"] is { NodeType: YamlNodeType.Mapping })
        {
            envs.AddRange(((YamlMappingNode)node1["env"]).Children.Select(
                    env => new EnvironmentVar() 
                        { Name = env.Key.ToString(), Value = env.Value.ToString() }
                )
            );
        }
        var binds=new List<Binding>();
        if (node1.Any(x=>x.Key.ToString()=="bindings")&&node1["bindings"] is { NodeType: YamlNodeType.Mapping })
        {
            var portIndex = 0;
            binds.AddRange(((YamlMappingNode)node1["bindings"]).Children.Select(
                    bind=>
                    {
                        portIndex++;
                        return new Binding()
                        {
                            Name = bind.Key.ToString(),
                            Protocol = bind.Value["protocol"].ToString(),
                            Scheme = bind.Value["scheme"].ToString(),
                            Transport = bind.Value["transport"].ToString(),
                            TargetPort = ((YamlMappingNode)bind.Value).Any(
                                x => x.Key.ToString() == "targetPort"
                            )
                                ? Convert.ToInt32(bind.Value["targetPort"].ToString())
                                : 5000+portIndex
                        };
                    }
                )
            );
        }
        
        if (isProject)
        {
            manifests.Add(new Manifest()
            {
                ServiceName = child.Key.ToString(),
                CustomImage = (manifest.Setting.Registry + "/" + manifest.Setting.Prefix + "-" + child.Key.ToString()).ToLower(),
                EnvironmentVars = envs.ToArray(),
                Bindings = binds.ToArray(),
                KubernetesName = manifest.Setting.Prefix+"-"+child.Key,
                ProjectName = child.Value["path"].ToString().Split('/')[0],
                History = history.ToArray(),
            });
        }
        if (type.Contains("container"))
        {
            manifests.Add(new Manifest()
            {
                ServiceName = child.Key.ToString(),
                CustomImage = node1["image"].ToString().ToLower(),
                EnvironmentVars = envs.ToArray(),
                Storages = (from node2 in (YamlSequenceNode)node1.Children["bindMounts"] 
                    select new Storage
                    {
                        PvName = node2["source"].ToString().Replace(".","-").Replace("/","-").Replace("\\","-").Replace("_","-"),
                        MountPath = node2["target"].ToString(),
                        ReadOnly = bool.Parse(node2["readOnly"].ToString()),
                        PvSize = "10Gi",
                        Access = Access.ReadWriteOnce
                    }).ToArray(),
                Bindings = binds.ToArray(),
                KubernetesName = manifest.Setting.Prefix+"-"+child.Key,
                ProjectName = null,
                History = history.ToArray(),
            });
        }

        manifest.Spec.Manifests = manifests.ToArray();
        for (int i = 0; i < manifests[^1].EnvironmentVars.Length; i++)
        {
            count = -1;
            while (count!=0)
            {
                var replaced=RawManifest(manifests[^1].EnvironmentVars[i].Value, manifestRoot: manifest);
                count=replaced.Item2;
                manifests[^1].EnvironmentVars[i].Value = replaced.Item1;
                manifest.Spec.Manifests = manifests.ToArray();
            }
        }
    }
    Console.WriteLine("Aspire Manifestの解析が終了しました．");
    
    WriteManifest(manifest);
}
rootCommand.AddCommand(prepareCommand);

var injectCommand = new Command("inject", "kubeploy2でデプロイするアプリケーションのユーザ指定環境変数を入力します．");
injectCommand.SetHandler(InjectCommandHandler);
void InjectCommandHandler(InvocationContext obj)
{
    var manifest = ReadManifest();
    
    string pattern = @"\{([^{}]+)\}";
    var count = 0;
    var uival = new List<EnvironmentVar>();
    var rex=Regex.Matches(manifest.Item2, pattern);
    Console.WriteLine("ユーザ指定環境変数が"+rex.Count+"個見つかりました．");
    foreach (var match in rex.ToArray())
    {
        if (!match.Success) throw new FormatException();
        var keyRaw = match.Groups[1].Value.Split(".");
        if (uival.Any(x=>x.Name == keyRaw[0]))continue;
        var val1 = "a";
        var val2 = "c";
        while (val1 != val2||val1 == null||val2==null)
        {
            Console.Write("ユーザ指定環境変数 "+keyRaw[0]+" の値を入力してください．：");
            val1 = Console.ReadLine();
            Console.Write("同じ値をもう一度入力してください．：");
            val2 = Console.ReadLine();
        }
        uival.Add(new EnvironmentVar{Name = keyRaw[0], Value = val1});
    }
    Console.WriteLine("ユーザ指定環境変数をすべて入力しました．checkコマンドを実行して，デプロイ可能か確認してください．");
    manifest.Item1.Spec.UserInputValue = uival.ToArray();
    WriteManifest(manifest.Item1);
}
rootCommand.AddCommand(injectCommand);

var checkCommand = new Command("check", "kubeploy2でデプロイする前のpreflight-checkです．");
checkCommand.SetHandler((CheckCommandHandler));
async Task CheckCommandHandler(InvocationContext obj)
{
    var manifest = ReadManifest();
    var kubeClient=KubernetesClient(manifest.Item1);
    var k8sNamespace = manifest.Item1.Setting.Namespace;
    var failer = (string res) =>
    {

        Console.WriteLine(
            $"指定されたKUBECONFIGのコンテキスト{manifest.Item1.Setting.Context}で名前空間{k8sNamespace}の"+res+"を取得できませんでした．");
        Console.WriteLine("KUBECONFIGや名前空間，権限の設定を見直してください．");
        Environment.Exit(1);
    };
    var version = kubeClient.Version.GetCode();
    Console.WriteLine($@"Go Version  : {version.GoVersion}
Git Version : {version.GitVersion}
Version     : {version.Major}.{version.Minor}
Compiler    : {version.Compiler}
Platform    : {version.Platform}");
    
    if (string.IsNullOrWhiteSpace(kubeClient.ListNamespacedPod(k8sNamespace).ApiVersion)) failer("Pod");
    if (string.IsNullOrWhiteSpace(kubeClient.ListNamespacedDeployment(k8sNamespace).ApiVersion)) failer("Deployment");
    if (string.IsNullOrWhiteSpace(kubeClient.ListNamespacedIngress(k8sNamespace).ApiVersion)) failer("Ingress");
    if (string.IsNullOrWhiteSpace(kubeClient.ListNamespacedSecret(k8sNamespace).ApiVersion)) failer("Secret");
    if (string.IsNullOrWhiteSpace(kubeClient.ListNamespacedService(k8sNamespace).ApiVersion)) failer("Service");
    if (string.IsNullOrWhiteSpace(kubeClient.ListNamespacedStatefulSet(k8sNamespace).ApiVersion)) failer("StatefulSet");
    Console.WriteLine("Kubernetesへの接続チェックに成功しました．");
    
    
    var docker = new DockerClientConfiguration(
            new Uri("unix:///var/run/docker.sock")
        )
        .CreateClient();
    var dockerVer = await docker.System.GetVersionAsync();
    if (dockerVer == null)
    {
        Console.WriteLine("Dockerへの接続に失敗しました．Dockerデーモンが起動していることを確認してください．");
        Environment.Exit(1);
    }
    Console.WriteLine($@"Go     Version : {dockerVer.GoVersion}
Git    Version : {dockerVer.GitCommit}
API    Version : {dockerVer.APIVersion}
Kernel Version : {dockerVer.KernelVersion}
Version/IsExp  : {dockerVer.Version}/{dockerVer.Experimental}
OS/Arch: {dockerVer.Os}/{dockerVer.Arch}");
    Console.WriteLine("Dockerへの接続チェックに成功しました．");
}
rootCommand.AddCommand(checkCommand);

var deployCommand = new Command("deploy", "Kubernetesクラスタにアプリケーションをデプロイします．");
var nowaitOpt = new Option<bool>("--no-wait", "Github RegistryのようにPrivateイメージをPublicイメージに変換する作業が完了するのを待つかどうか．");
var noimageOpt = new Option<bool>("--no-image", "Dockerイメージをビルドするかどうか．");
deployCommand.AddOption(nowaitOpt);
deployCommand.AddOption(noimageOpt);
deployCommand.SetHandler((DeployCommandHandler));
async Task DeployCommandHandler(InvocationContext obj)
{
    var manifest = ReadManifest();
    var k8sNamespace = manifest.Item1.Setting.Namespace;
    var docker = new DockerClientConfiguration(
            new Uri("unix:///var/run/docker.sock")
        )
        .CreateClient();
    var kube=KubernetesClient(manifest.Item1);
    kube.GetCode();
    
    if (!obj.ParseResult.GetValueForOption(noimageOpt))
    {
        File.WriteAllText(".dockerignore",@"**/.dockerignore
**/.env
**/.git
**/.gitignore
**/.project
**/.settings
**/.toolstarget
**/.vs
**/.vscode
**/.idea
**/*.*proj.user
**/*.dbmdl
**/*.jfm
**/azds.yaml
**/bin
**/charts
**/docker-compose*
**/Dockerfile*
**/node_modules
**/npm-debug.log
**/obj
**/secrets.dev.yaml
**/values.dev.yaml
LICENSE
README.md");
        foreach (var project in manifest.Item1.Spec.Manifests.Where(x=>x.ProjectName !=null))
        {
            #region Dockerfile

            var path = project.ProjectName + "/" + project.ProjectName + ".csproj";
            //(project.Bindings.Any(x=>x.Transport.Contains("http"))?"aspnet":"runtime")
            var dockerfile =
                @"FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY . .
RUN dotnet restore """+path+@"""

WORKDIR ""/src""
RUN dotnet build """+path+@""" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish """+path+@""" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false
RUN dotnet dev-certs https -ep /app/publish/aspnetapp.pfx -p internalcred&&chmod 777 /app/publish/aspnetapp.pfx

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

ENV ""ASPNETCORE_Kestrel__Certificates__Default__Password"" ""internalcred""
ENV ""ASPNETCORE_Kestrel__Certificates__Default__Path"" ""/app/aspnetapp.pfx""
ENTRYPOINT [""dotnet"", """+project.ProjectName+@".dll""]";
            #endregion
            File.WriteAllText("Dockerfile",dockerfile);
            File.WriteAllText(project.ProjectName+"/Dockerfile",dockerfile);
            var tag = "v0.0.1-None";
            var major = 0;
            var minor = 0;
            var patch = 1;
            var preview = PreviewType.None;
            if (!Equals(project.History, null) && project.History.Length != 0)
            {
                major = project.History[^1].MajorVersion;
                minor = project.History[^1].MinorVersion;
                patch = project.History[^1].Patch + 1;
                preview = project.History[^1].Preview;
                tag = "v" + major + "." + minor + "." + patch + "-" + preview;
            }

            tag = tag.ToLower();
            if (Equals(project.History, null)) project.History =new List<History>().ToArray();
            var history=new List<History>(project.History);
            history.Add(new History(){Date = DateTime.UtcNow.AddHours(9),MajorVersion = major,MinorVersion = minor,Patch = patch,Preview = preview});
            var imageName = project.CustomImage + ":" + tag;
            var builder= Process.Start("docker",
                "build . -t " + imageName);
            builder.WaitForExit();
            Console.WriteLine(builder.ExitCode);
            if (builder.ExitCode != 0) Environment.Exit(builder.ExitCode);
            Console.WriteLine("Pushing Image");
            var pusher= Process.Start("docker", "push " + imageName);
            pusher.WaitForExit();
            Console.WriteLine(pusher.ExitCode);
            if (pusher.ExitCode != 0) Environment.Exit(pusher.ExitCode);
            Console.WriteLine("Pushed Image");
            manifest.Item1.Spec.Manifests.Single(x=>x.ProjectName==project.ProjectName).History=history.ToArray();
        }
    }
    WriteManifest(manifest.Item1);
    if (!obj.ParseResult.GetValueForOption(nowaitOpt))
    {
        Console.WriteLine("アクセス権を変更してください．変更したら，Enterをおしてください．");
        Console.Read();
    }
    foreach (var project in manifest.Item1.Spec.Manifests)
    {
        var baseName = project.KubernetesName.ToLower();
        var secretofProject = new Dictionary<string, string>();
        var envs = new List<V1EnvFromSource>();
        var envdt = new List<V1EnvVar>();
        string pattern = @"\{([^{}]+)\}";
        foreach (var env in project.EnvironmentVars)
        {
            secretofProject.Add(
                env.Name,
                Regex.Replace(
                    env.Value,
                    pattern,
                    x =>
                    {
                        if (!x.Success) throw new FormatException();
                        var keyRaw = x.Groups[1].Value.Split(".")[0];
                        return manifest.Item1.Spec.UserInputValue.Single(y => y.Name == keyRaw).Value;
                    }
                )
            );
            envs.Add(new V1EnvFromSource()
            {
                SecretRef = new V1SecretEnvSource()
                {
                    Name = baseName+"-secret",
                    Optional = false
                },
                ConfigMapRef = new V1ConfigMapEnvSource(),
                Prefix = env.Name
            });
            envdt.Add(new V1EnvVar()
            {
                Name = env.Name,
                ValueFrom = new V1EnvVarSource()
                {
                    SecretKeyRef = new V1SecretKeySelector()
                    {
                        Name = baseName+"-secret",
                        Key = env.Name,
                        Optional = false
                    }
                },
                Value = ""
            });
        }
        var containerPort = new List<V1ContainerPort>();
        var servicePort = new List<V1ServicePort>();
        foreach (var bind in project.Bindings)
        {
            var protocol = "TCP";
            if (bind.Protocol.ToLower()=="udp") protocol = "UDP";
            else if (bind.Protocol.ToLower() == "sctp") protocol = "SCTP";
            
            containerPort.Add(new V1ContainerPort() { Name = bind.Name.ToLower(), ContainerPort = bind.Port, Protocol = protocol});
            servicePort.Add(new V1ServicePort() { Name = bind.Name.ToLower(), Port = bind.Port, Protocol = protocol, TargetPort = bind.Port, AppProtocol = bind.Protocol});
        }

        var volumeMounts = new List<V1VolumeMount>();
        var volumes = new List<V1Volume>();
        if (Equals(project.Storages, null)) project.Storages=new List<Storage>().ToArray();
        foreach (var storage in project.Storages)
        {
            if (!kube.CoreV1.ListNamespacedPersistentVolumeClaim(
                    k8sNamespace
                ).Items.Any(x=>x.Metadata.Name==baseName + "-" + storage.PvName.ToLower() + "-pvc"))
            {
                kube.CoreV1.CreateNamespacedPersistentVolumeClaim(new V1PersistentVolumeClaim()
                    {
                        Metadata = new V1ObjectMeta()
                        {
                            Name = baseName + "-" + storage.PvName.ToLower() + "-pvc"
                        },
                        Spec = new V1PersistentVolumeClaimSpec()
                        {
                            StorageClassName = manifest.Item1.Setting.StorageClass,
                            Resources = new V1VolumeResourceRequirements()
                            {
                                Requests = new Dictionary<string, ResourceQuantity>()
                                {
                                    ["storage"] = new ResourceQuantity(decimal.Parse("10"),9,ResourceQuantity.SuffixFormat.DecimalSI)
                                }
                            },
                            VolumeMode = "Filesystem",
                            AccessModes = new List<string>()
                            {
                                storage.Access.ToString()
                            }
                        }
                    }
                    , k8sNamespace
                );
            }
            volumeMounts.Add(new V1VolumeMount()
            {
                Name = storage.PvName.ToLower(),
                MountPath = storage.MountPath,
                ReadOnlyProperty = storage.ReadOnly
            });
            volumes.Add(new V1Volume()
            {
                Name = storage.PvName.ToLower(),
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource()
                {
                    ClaimName = baseName + "-" + storage.PvName.ToLower() + "-pvc",
                    ReadOnlyProperty = storage.ReadOnly,
                }
            });
        }
        
        if (containerPort.Count != 0)
        {
            if (kube.CoreV1.ListNamespacedService( k8sNamespace).Items.Any(x=>x.Metadata.Name==baseName + "-svc"))
                kube.CoreV1.DeleteNamespacedService(baseName + "-svc", k8sNamespace);
            kube.CoreV1.CreateNamespacedService(new V1Service()
            {
                Metadata = new V1ObjectMeta()
                {
                    Name = baseName+"-svc"
                },
                ApiVersion = "v1",
                Spec = new V1ServiceSpec()
                {
                    Type = "LoadBalancer",
                    Selector =new Dictionary<string, string>()
                    {
                        ["dotnet"]=baseName
                    },
                    Ports = servicePort.ToArray()
                }
            } ,k8sNamespace);
        }

        if (project.Bindings.Any(x => x.Scheme.ToLower().Contains("http")))
        {
            if (kube.NetworkingV1.ListNamespacedIngress(k8sNamespace).Items.Any(x=>x.Metadata.Name==baseName + "-ingress"))
                kube.NetworkingV1.DeleteNamespacedIngress(baseName + "-ingress", k8sNamespace);
            var port = project.Bindings.Where(x => x.Scheme.ToLower().Contains("http"));
            Binding? usePort = null;
            if (port.Any(x => x.Scheme.ToLower() == "https"))
            {
                usePort = port.Where(x => x.Scheme.ToLower() == "https").Take(1).ToArray()[0];
            }
            else usePort = port.Take(1).ToArray()[0];
            kube.NetworkingV1.CreateNamespacedIngress(new V1Ingress()
            {
                Metadata = new V1ObjectMeta()
                {
                    Name = baseName+"-ingress",
                    Annotations =new Dictionary<string, string>()
                    {
                        ["nginx.ingress.kubernetes.io/backend-protocol"]= "https",
                        ["nginx.ingress.kubernetes.io/proxy-buffer-size"]= "128k",
                        ["kubernetes.io/tls-acme"]= "true",
                        ["cert-manager.io/cluster-issuer"]= "letsencrypt-issuer"
                    },
                    Labels =new Dictionary<string, string>()
                    {
                        ["name"] = baseName
                    }
                },
                ApiVersion = "networking.k8s.io/v1",
                Spec = new V1IngressSpec()
                {
                    Tls =new List<V1IngressTLS>()
                    {
                        new V1IngressTLS()
                        {
                            Hosts = new List<string>(){baseName+".s.secuaos.work"},
                            SecretName = baseName+"-ingress-secret"
                        }
                    },
                    IngressClassName = "nginx",
                    Rules =new List<V1IngressRule>()
                    {
                        new V1IngressRule()
                        {
                            Host = baseName+".s.secuaos.work",
                            Http = new V1HTTPIngressRuleValue()
                            {
                                Paths =new List<V1HTTPIngressPath>()
                                {
                                    new ()
                                    {
                                        PathType = "Prefix",
                                        Path = "/",
                                        Backend = new V1IngressBackend()
                                        {
                                            Service = new V1IngressServiceBackend()
                                            {
                                                Name = baseName+"-svc",
                                                Port = new V1ServiceBackendPort()
                                                {
                                                    Number =  usePort.Port
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            } ,k8sNamespace);
        }
        
        if (kube.CoreV1.ListNamespacedSecret(k8sNamespace).Items.Any(x=>x.Metadata.Name==baseName + "-secret"))
            kube.CoreV1.DeleteNamespacedSecret(baseName + "-secret", k8sNamespace);
        kube.CoreV1.CreateNamespacedSecret(new V1Secret()
        {
            Metadata = new V1ObjectMeta()
            {
                Name = baseName+"-secret"
            },
            ApiVersion = "v1",
            StringData =secretofProject,
            Type = "Opaque"
        },k8sNamespace);
        
        if (kube.AppsV1.ListNamespacedDeployment(k8sNamespace).Items.Any(x=>x.Metadata.Name==baseName + "-deploy"))
            kube.AppsV1.DeleteNamespacedDeployment(baseName + "-deploy", k8sNamespace);
        
        var tag = project.History is not { Length: > 0 }? "": (":v" + project.History[^1].MajorVersion + "." +
                                                               project.History[^1].MinorVersion + "." +
                                                               project.History[^1].Patch + "-" + project.History[^1].Preview).ToLower();

        var imagepullsecret =
            project.ProjectName != null && !string.IsNullOrWhiteSpace(manifest.Item1.Setting.ImagePullSecret)
                ? new List<V1LocalObjectReference>()
                {
                    new V1LocalObjectReference()
                    {
                        Name = manifest.Item1.Setting.ImagePullSecret
                    }
                }
                : new List<V1LocalObjectReference>() { };
        kube.AppsV1.CreateNamespacedDeployment(new V1Deployment()
        {
            Metadata = new V1ObjectMeta()
            {
                Name = baseName+"-deploy"
            },
            Spec = new V1DeploymentSpec()
            {
                Replicas = 1,
                Selector = new V1LabelSelector()
                {
                    MatchLabels =new Dictionary<string, string>()
                    {
                        ["dotnet"] = baseName
                    }
                },
                Strategy = new V1DeploymentStrategy(),
                Template = new V1PodTemplateSpec()
                {
                    Metadata = new V1ObjectMeta()
                    {
                        Name = baseName,
                        Labels =new Dictionary<string, string>()
                        {
                            ["dotnet"]=baseName
                        }
                    },
                    Spec = new V1PodSpec()
                    {
                        ImagePullSecrets = imagepullsecret,
                        RestartPolicy = "Always",
                        Containers =new List<V1Container>()
                        {
                            new V1Container()
                            {
                                Name = baseName,
                                Image = project.CustomImage+tag,
                                Env = envdt,
                                Ports = containerPort.ToArray(),
                                VolumeMounts = volumeMounts.ToArray()
                            }
                        },
                        Volumes = volumes.ToArray()
                    }
                }
            }
        },k8sNamespace);
    }
    
    WriteManifest(manifest.Item1);
}
rootCommand.AddCommand(deployCommand);

var undeployCommand = new Command("undeploy", "Kubernetesクラスタにデプロイしたアプリケーションをアンデプロイします．");
undeployCommand.SetHandler(UnDeployCommandHandler);

void UnDeployCommandHandler(InvocationContext obj)
{
    var manifest = ReadManifest();
    Console.Write($@"アプリケーション{manifest.Item1.Setting.ApplicationName}をKubernetesクラスター{manifest.Item1.Setting.Context}からアンデプロイしますか？ (y/n)");
    var doContinue = Console.ReadKey();
    Console.WriteLine();
    if (doContinue.Key != ConsoleKey.Y)
    {
        Console.WriteLine("キャンセルされました．");
        return;
    }
    var kube=KubernetesClient(manifest.Item1);
    var k8sNamespace=manifest.Item1.Setting.Namespace;
    
    foreach (var project in manifest.Item1.Spec.Manifests)
    {
        var baseName = project.KubernetesName.ToLower();

        if (!Equals(project.Storages, null))
        {
            foreach (var storage in project.Storages)
            {
                if(kube.CoreV1.ListNamespacedPersistentVolumeClaim(k8sNamespace)
                   .Items.Any(x => x.Metadata.Name == baseName + "-" + storage.PvName.ToLower() + "-pvc"))
                {
                    kube.CoreV1.DeleteNamespacedPersistentVolumeClaim(baseName + "-" + storage.PvName.ToLower() + "-pvc", k8sNamespace);
                }
            }
        }
        
        if (project.Bindings.Length != 0)
        {
            if (kube.CoreV1.ListNamespacedService( k8sNamespace).Items.Any(x=>x.Metadata.Name==baseName + "-svc"))
                kube.CoreV1.DeleteNamespacedService(baseName + "-svc", k8sNamespace);
        }

        if (project.Bindings.Any(x => x.Scheme.ToLower().Contains("http")))
        {
            if (kube.NetworkingV1.ListNamespacedIngress(k8sNamespace).Items.Any(x=>x.Metadata.Name==baseName + "-ingress"))
                kube.NetworkingV1.DeleteNamespacedIngress(baseName + "-ingress", k8sNamespace);
        }
        
        if (kube.CoreV1.ListNamespacedSecret(k8sNamespace).Items.Any(x=>x.Metadata.Name==baseName + "-secret"))
            kube.CoreV1.DeleteNamespacedSecret(baseName + "-secret", k8sNamespace);
        
        if (kube.AppsV1.ListNamespacedDeployment(k8sNamespace).Items.Any(x=>x.Metadata.Name==baseName + "-deploy"))
            kube.AppsV1.DeleteNamespacedDeployment(baseName + "-deploy", k8sNamespace);
    }
}
rootCommand.AddCommand(undeployCommand);

await rootCommand.InvokeAsync(args);
return;

Kubernetes KubernetesClient(Root manifest)
{
    
    var kubeconfig=manifest.Setting.Kubeconfig;
    if (kubeconfig[..2]=="~/") kubeconfig=kubeconfig.Replace("~/",Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)+"/");
    var config =
        KubernetesClientConfiguration.BuildConfigFromConfigFile(
            kubeconfig,
            manifest.Setting.Context,
            useRelativePaths: true
        );
    return new Kubernetes(config);
}

void WriteManifest(Root manifest)
{
    Console.WriteLine("Kubeploy2 Manifestを書き出しています．");
    var serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();
    var yaml = serializer.Serialize(manifest);
    File.WriteAllText(MANIFEST_FILE, yaml);
}

(Root,string) ReadManifest()
{
    Console.WriteLine("Kubeploy2 Manifestを読み込んでいます．");
    var deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();
    var input = File.ReadAllText(MANIFEST_FILE);
    return (deserializer.Deserialize<Root>(input),input);
}

(string, int) RawManifest(string manifest, YamlNode? root=null,Root? manifestRoot=null)
{
    string pattern = @"\{([^{}]+)\}";
    var count = 0;
    var res=Regex.Replace(manifest, pattern, x =>
    {
        if (!x.Success) throw new FormatException();
        var keyRaw = x.Groups[1].Value.Split(".");
        if (manifestRoot!=null)
        {
            if (keyRaw.Length == 3) return x.Value;
            var svc=keyRaw[0];
            var schema=keyRaw[2];
            var val = keyRaw[3];
            var bindingData=manifestRoot.Spec.Manifests.Single(y => y.ServiceName == svc).Bindings.Single(y => y.Scheme == schema);
            switch (val)
            {
                case "targetPort":
                    return bindingData.Port.ToString();
                case "port":
                    return bindingData.Port.ToString();
                case "host":
                    return manifestRoot.Setting.Prefix + "-" + svc+"-svc";
                case "url":
                    return schema + "://" + manifestRoot.Setting.Prefix + "-" + svc + ":" + bindingData.Port;
            }
        }

        if (root != null)
        {
            var key=keyRaw.Aggregate<string,YamlNode?>(root, (y, z) =>
            {
                if (y?.NodeType == YamlNodeType.Mapping && ((YamlMappingNode)y).Children.All(p => p.Key.ToString() != z))
                {
                    return null;
                }
                return y?[z];
            });
            if (key is not { NodeType: YamlNodeType.Scalar }) return x.Value;
            count++;
            return key.ToString();
        }
        throw new FormatException();
    });
    return (res,count);
}


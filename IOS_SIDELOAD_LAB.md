# FluxoPrime iOS Sideload Lab

Objetivo: gerar um IPA de teste sem Apple Developer pago e instalar no iPhone com Sideloadly/AltStore usando Apple ID gratuito.

## Limites importantes

- Esse fluxo e apenas para laboratorio.
- O app instalado com Apple ID gratuito tende a expirar em cerca de 7 dias.
- Para TestFlight, App Store ou uso real com clientes, ainda sera necessario Apple Developer Program pago.
- O iOS ainda exige assinatura. Sideloadly/AltStore assinam o IPA localmente com o Apple ID gratuito.

## Fluxo

1. Subir o projeto para um repositorio GitHub/GitLab/Bitbucket.
2. Conectar o repositorio no Codemagic.
3. Rodar o workflow `maui-ios-unsigned-sideload-lab`.
4. Baixar o artifact `FluxoPrimeTV-ios-unsigned-*.ipa`.
5. Instalar no iPhone com Sideloadly ou AltStore.
6. No iPhone, confiar no perfil do desenvolvedor se o iOS pedir:
   - Ajustes > Geral > VPN e Gerenciamento de Dispositivo.

## Workflow alternativo

Se o build unsigned para aparelho real falhar por exigencia de code signing, rode primeiro `maui-ios-simulator-smoke`. Ele valida compilacao iOS sem assinar para iPhone real.

## Arquivos de suporte

- `codemagic.yaml`: workflows do Codemagic.
- `projeto ios/FluxoPrimeIos/Platforms/iOS/Info.plist`: configuracao iOS, incluindo liberacao temporaria de HTTP para streams de IPTV durante testes.
- `projeto ios/FluxoPrimeIos/FluxoPrimeIos.csproj`: app MAUI iOS-only usado pelo Codemagic.
- `projeto ios/FluxoPrimeCore/FluxoPrimeCore.csproj`: core compartilhado copiado para o pacote iOS.

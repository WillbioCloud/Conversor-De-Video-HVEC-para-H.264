using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace ConversorDesktop;

public partial class MainWindow : FluentWindow
{
    private string _arquivoSelecionado = "";
    private TimeSpan _duracaoTotalVideo = TimeSpan.Zero;
    public MainWindow()
    {
        InitializeComponent();
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
    }

    private async void BtnSelecionarVideo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecione o vídeo da Apple",
            Filter = "Vídeos (*.mov;*.mp4)|*.mov;*.mp4|Todos os arquivos (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            _arquivoSelecionado = dialog.FileName;
            TxtCaminhoVideo.Text = _arquivoSelecionado;
            TxtLogs.Text = "Analisando metadados do arquivo, aguarde...\n";

            // Executa a leitura do vídeo sem travar a interface (Async)
            string detalhes = await ObterDetalhesVideo(_arquivoSelecionado);
            TxtLogs.Text = detalhes;
        }
    }

    private async void BtnConverter_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_arquivoSelecionado))
        {
            TxtLogs.AppendText("\n[ERRO]: Por favor, selecione um vídeo primeiro.\n");
            return;
        }

        // 1. Prepara caminhos padrão
        string pastaDestinoOriginal = Path.GetDirectoryName(_arquivoSelecionado) ?? "";
        string nomeOriginalSemExtensao = Path.GetFileNameWithoutExtension(_arquivoSelecionado);

        // 2. Abre a caixa de diálogo para salvar
        var saveDialog = new SaveFileDialog
        {
            Title = "Salvar vídeo convertido como...",
            Filter = "Vídeo MP4 (*.mp4)|*.mp4",
            FileName = $"{nomeOriginalSemExtensao}_android.mp4",
            InitialDirectory = pastaDestinoOriginal
        };

        if (saveDialog.ShowDialog() != true)
        {
            return; // Usuário fechou a janela ou cancelou
        }

        string arquivoSaida = saveDialog.FileName;

        // 3. Trava a Interface
        BtnConverter.IsEnabled = false;
        BtnConverter.Content = "Convertendo...";
        LblStatus.Text = "Processando: 0.0%";
        ProgressoConversao.IsIndeterminate = false; // Barra agora será exata
        ProgressoConversao.Value = 0;
        
        // 4. Verifica a resolução escolhida
        string resolucaoSelecionada = (CmbResolucao.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "original";
        string comandoScale = "";
        
        if (resolucaoSelecionada == "1080")
        {
            comandoScale = "-vf \"scale='min(1920,iw)':-2\"";
        }
        else if (resolucaoSelecionada == "720")
        {
            comandoScale = "-vf \"scale='min(1280,iw)':-2\"";
        }

        TxtLogs.AppendText($"\n[INICIANDO] Codificando para:\n{arquivoSaida}\n");

        // 5. Descobre a marca do processador para tentar usar a GPU Integrada primeiro
        string processorId = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
        string[] encodersParaTestar;

        if (processorId.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            encodersParaTestar = new[] { "h264_qsv", "libx264" }; // Tenta Intel Quick Sync, se der erro cai pra CPU
        else if (processorId.Contains("AMD", StringComparison.OrdinalIgnoreCase))
            encodersParaTestar = new[] { "h264_amf", "libx264" }; // Tenta AMD AMF, se der erro cai pra CPU
        else
            encodersParaTestar = new[] { "libx264" };

        // 6. Executa o FFmpeg com sistema de Fallback e Controle Térmico
        bool sucesso = await Task.Run(async () =>
        {
            foreach (var encoder in encodersParaTestar)
            {
                try
                {
                    Application.Current.Dispatcher.Invoke(() => TxtLogs.AppendText($"\n[MOTOR] Tentando encoder: {encoder}...\n"));

                    string encoderArgs = "";
                    if (encoder == "h264_qsv") 
                        encoderArgs = "-c:v h264_qsv -preset veryfast -b:v 4M"; // QSV não lida bem com CRF
                    else if (encoder == "h264_amf") 
                        encoderArgs = "-c:v h264_amf -quality speed -b:v 4M"; // AMF prefere bitrate fixo
                    else 
                    {
                        // MODO SEGURO (FALLBACK): Usa só metade dos núcleos do PC para não desligar por calor
                        int threadsSeguras = Math.Max(1, Environment.ProcessorCount / 2);
                        encoderArgs = $"-c:v libx264 -preset ultrafast -crf 23 -threads {threadsSeguras}";
                    }

                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                            Arguments = $"-y -hwaccel auto -i \"{_arquivoSelecionado}\" {encoderArgs} -pix_fmt yuv420p {comandoScale} -c:a aac -b:a 192k -movflags +faststart \"{arquivoSaida}\"",
                            UseShellExecute = false,
                            RedirectStandardError = true, // O FFmpeg cospe o progresso no StandardError
                            CreateNoWindow = true
                        }
                    };

                    process.Start();

                    // Lê o terminal invisível linha por linha enquanto o conversor roda
                    using (var reader = process.StandardError)
                    {
                        while (!reader.EndOfStream)
                        {
                            string? linha = await reader.ReadLineAsync();
                            if (linha != null && linha.Contains("time="))
                            {
                                var match = Regex.Match(linha, @"time=(\d{2}:\d{2}:[0-9\.]+)");
                                if (match.Success && _duracaoTotalVideo.TotalSeconds > 0)
                                {
                                    if (TimeSpan.TryParse(match.Groups[1].Value, out TimeSpan tempoAtual))
                                    {
                                        double porcentagem = (tempoAtual.TotalSeconds / _duracaoTotalVideo.TotalSeconds) * 100;
                                        if (porcentagem > 100) porcentagem = 100;

                                        // Atualiza a UI na Thread Principal
                                        Application.Current.Dispatcher.Invoke(() =>
                                        {
                                            ProgressoConversao.Value = porcentagem;
                                            LblStatus.Text = $"Processando: {porcentagem:F1}%";
                                        });
                                    }
                                }
                            }
                        }
                    }

                    await process.WaitForExitAsync();
                    
                    if (process.ExitCode == 0)
                        return true; // Sucesso! Sai do foreach e não testa o próximo.
                }
                catch
                {
                    // Se o Start() falhar criticamente, o loop ignora e vai para o próximo encoder da lista.
                }
            }
            
            return false; // Se testou a GPU e a CPU e tudo falhou
        });

        // 7. Destrava a Interface
        BtnConverter.IsEnabled = true;
        BtnConverter.Content = "Iniciar Conversão";

        if (sucesso)
        {
            ProgressoConversao.Value = 100;
            LblStatus.Text = "Conversão finalizada com sucesso!";
            TxtLogs.AppendText($"[CONCLUÍDO] Arquivo salvo em:\n{arquivoSaida}\n");
        }
        else
        {
            ProgressoConversao.Value = 0;
            LblStatus.Text = "Erro na conversão!";
            TxtLogs.AppendText("[FALHA] O FFmpeg retornou um erro ou foi interrompido.\n");
        }
    }

    // Rotina que consome o FFmpeg nativamente no fundo para roubar as informações
    private async Task<string> ObterDetalhesVideo(string caminhoArquivo)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                    Arguments = $"-hide_banner -i \"{caminhoArquivo}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            
            // O FFmpeg imprime metadados na saída de Erro (StandardError), não no Output padrão
            string output = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Pega a linha do tempo completa (00:00:00.00)
            string duracaoStr = Regex.Match(output, @"Duration:\s*(\d{2}:\d{2}:[0-9\.]+)").Groups[1].Value;
            if (TimeSpan.TryParse(duracaoStr, out TimeSpan parsedTempo))
            {
                _duracaoTotalVideo = parsedTempo;
            }

            // Isola a string completa do vídeo e do áudio
            string videoStream = Regex.Match(output, @"Stream #\d:\d.*?: Video: (.*)").Groups[1].Value;
            string audioStream = Regex.Match(output, @"Stream #\d:\d.*?: Audio: (.*)").Groups[1].Value;

            var sb = new StringBuilder();
            sb.AppendLine("📋 DETALHES DO ARQUIVO");
            sb.AppendLine($"Nome: {Path.GetFileName(caminhoArquivo)}");
            sb.AppendLine($"Tamanho: {new FileInfo(caminhoArquivo).Length / 1024 / 1024} MB");
            
            if (!string.IsNullOrEmpty(duracaoStr))
                sb.AppendLine($"Duração: {duracaoStr}");

            if (!string.IsNullOrEmpty(videoStream))
            {
                var videoParts = videoStream.Split(',').Select(p => p.Trim()).ToList();
                string codec = videoParts.FirstOrDefault()?.Split(' ')[0] ?? "Desconhecido";
                string resolution = videoParts.FirstOrDefault(p => Regex.IsMatch(p, @"\d{3,4}x\d{3,4}"))?.Split(' ')[0] ?? "?";
                string fps = videoParts.FirstOrDefault(p => p.Contains("fps")) ?? "";
                sb.AppendLine($"Vídeo: Codec {codec.ToUpper()} | {resolution} | {fps}");
            }

            if (!string.IsNullOrEmpty(audioStream))
            {
                var audioParts = audioStream.Split(',').Select(p => p.Trim()).ToList();
                string codecAudio = audioParts.FirstOrDefault()?.Split(' ')[0] ?? "Desconhecido";
                string freq = audioParts.FirstOrDefault(p => p.Contains("Hz")) ?? "";
                string canais = audioParts.FirstOrDefault(p => p.Contains("stereo") || p.Contains("mono")) ?? "";
                sb.AppendLine($"Áudio: Codec {codecAudio.ToUpper()} | {freq} | {canais}");
            }

            return sb.ToString();
        }
        catch
        {
            // Fallback caso o comando falhe ou o Windows não ache o FFmpeg no Path global
            return $"📋 DETALHES BÁSICOS\n" +
                   $"Nome: {Path.GetFileName(caminhoArquivo)}\n" +
                   $"Tamanho: {new FileInfo(caminhoArquivo).Length / 1024 / 1024} MB\n" +
                   $"\n⚠️ (Adicione o FFmpeg ao PATH do Windows para ver metadados de codecs e resolução)";
        }
    }
}
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Api.Models
{
    public class GetTokenRequest
    {
        [Required]
        public string name { get; set; }

        [Required]
        public string password { get; set; }
    }
}
